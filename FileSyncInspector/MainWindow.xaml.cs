using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Media;

namespace FileSyncInspector
{
    public partial class MainWindow : Window
    {
        private CancellationTokenSource _cts;

        public MainWindow()
        {
            InitializeComponent();
        }

        private void ButtonBrowseSource_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "Vyberte zdrojovou složku", // Lze nahradit Strings.SelectSourceFolder
                ShowNewFolderButton = false
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                TextBoxSourceFolder.Text = dialog.SelectedPath;
                UpdateStartButtonState();
            }
        }

        private void ButtonBrowseTarget_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "Vyberte cílovou složku", // Lze nahradit Strings.SelectTargetFolder
                ShowNewFolderButton = false
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                TextBoxTargetFolder.Text = dialog.SelectedPath;
                UpdateStartButtonState();
            }
        }

        private void UpdateStartButtonState()
        {
            ButtonStart.IsEnabled = !string.IsNullOrWhiteSpace(TextBoxSourceFolder.Text)
                                  && !string.IsNullOrWhiteSpace(TextBoxTargetFolder.Text)
                                  && TextBoxSourceFolder.Text != TextBoxTargetFolder.Text;
        }

        private async void ButtonStart_Click(object sender, RoutedEventArgs e)
        {
            ButtonStart.IsEnabled = false;
            ButtonCancel.IsEnabled = true;
            ProgressBarComparison.Value = 0;
            ProgressBarComparison.IsIndeterminate = true;
            TextBlockCurrentOperation.Text = "Připravuji..."; // Lze nahradit Strings.Preparing

            _cts = new CancellationTokenSource();

            TextBoxSourceOutput.Clear();
            TextBoxTargetOutput.Clear();
            TreeViewSourceOutput.Items.Clear();
            TreeViewTargetOutput.Items.Clear();

            string sourcePath = TextBoxSourceFolder.Text;
            string targetPath = TextBoxTargetFolder.Text;

            try
            {
                await Task.Run(() => CompareFoldersAsync(sourcePath, targetPath, _cts.Token));
            }
            catch (OperationCanceledException)
            {
                TextBlockCurrentOperation.Text = "Zrušeno uživatelem."; // Lze nahradit Strings.CancelledByUser
                TextBoxSourceOutput.Text = "Porovnávání bylo zrušeno uživatelem.";
                TextBoxTargetOutput.Text = "Porovnávání bylo zrušeno uživatelem.";
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Kritická chyba: {ex.Message}", "Chyba", MessageBoxButton.OK, MessageBoxImage.Error);
                TextBlockCurrentOperation.Text = "Nastala chyba.";
            }
            finally
            {
                ButtonStart.IsEnabled = true;
                ButtonCancel.IsEnabled = false;
                ProgressBarComparison.IsIndeterminate = false;
                ProgressBarComparison.Value = 0;
                _cts?.Dispose();
                _cts = null;
            }
        }

        private void ButtonCancel_Click(object sender, RoutedEventArgs e)
        {
            if (_cts != null && !_cts.IsCancellationRequested)
            {
                ButtonCancel.IsEnabled = false; // Zabráníme vícenásobnému kliknutí
                TextBlockCurrentOperation.Text = "Zastavuji proces..."; // Strings.StoppingProcess
                _cts.Cancel();
            }
        }

        private void ToggleView_Click(object sender, RoutedEventArgs e)
        {
            if (TreeViewPanel.Visibility == Visibility.Visible)
            {
                TreeViewPanel.Visibility = Visibility.Collapsed;
                TextOutputPanel.Visibility = Visibility.Visible;
            }
            else
            {
                TreeViewPanel.Visibility = Visibility.Visible;
                TextOutputPanel.Visibility = Visibility.Collapsed;
            }
        }

        // --- CORE LOGIKA POROVNÁVÁNÍ (Běží v Task.Run na pozadí) ---

        private async Task CompareFoldersAsync(string sourcePath, string targetPath, CancellationToken token)
        {
            var sourceDirsRel = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var targetDirsRel = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sourceFilesRel = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var targetFilesRel = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Stopwatch chrání UI před Dispatcher floodem (updatuje max 1x za 60ms)
            var uiThrottle = Stopwatch.StartNew();

            Action<string> reportProgressText = (text) =>
            {
                if (uiThrottle.ElapsedMilliseconds > 60)
                {
                    Dispatcher.InvokeAsync(() => TextBlockCurrentOperation.Text = text);
                    uiThrottle.Restart();
                }
            };

            // 1. Bezpečné načtení struktury (Manuální iterace pro možnost okamžitého zrušení)
            ScanDirectory(sourcePath, sourceDirsRel, sourceFilesRel, token, path => reportProgressText($"Skenuji zdroj: {path}"));
            ScanDirectory(targetPath, targetDirsRel, targetFilesRel, token, path => reportProgressText($"Skenuji cíl: {path}"));

            var commonFiles = sourceFilesRel.Intersect(targetFilesRel).ToList();

            int totalSteps = sourceDirsRel.Count + targetDirsRel.Count + sourceFilesRel.Count + targetFilesRel.Count + commonFiles.Count;
            if (totalSteps == 0) totalSteps = 1;

            int currentStep = 0;
            int lastProgress = -1;

            Dispatcher.Invoke(() => ProgressBarComparison.IsIndeterminate = false);

            void UpdateProgress(string currentFile)
            {
                reportProgressText($"Porovnávám: {currentFile}");

                int progress = (int)((double)currentStep / totalSteps * 100);
                if (progress != lastProgress)
                {
                    lastProgress = progress;
                    Dispatcher.InvokeAsync(() => ProgressBarComparison.Value = progress);
                }
            }

            var missingInTargetDirs = new List<string>();
            var missingInSourceDirs = new List<string>();
            var missingInTargetFiles = new List<string>();
            var missingInSourceFiles = new List<string>();
            var differentFiles = new List<string>();

            var sourceRoot = new FileSystemItem { Name = System.IO.Path.GetFileName(sourcePath), FullPath = sourcePath };
            var targetRoot = new FileSystemItem { Name = System.IO.Path.GetFileName(targetPath), FullPath = targetPath };

            // 2. Porovnání složek a existencí
            foreach (var dir in sourceDirsRel)
            {
                token.ThrowIfCancellationRequested();
                if (!targetDirsRel.Contains(dir))
                {
                    missingInTargetDirs.Add(dir);
                    AddPathToTree(sourceRoot, System.IO.Path.Combine(sourcePath, dir), sourcePath, "missing");
                }
                currentStep++; UpdateProgress(dir);
            }

            foreach (var dir in targetDirsRel)
            {
                token.ThrowIfCancellationRequested();
                if (!sourceDirsRel.Contains(dir))
                {
                    missingInSourceDirs.Add(dir);
                    AddPathToTree(targetRoot, System.IO.Path.Combine(targetPath, dir), targetPath, "added");
                }
                currentStep++; UpdateProgress(dir);
            }

            foreach (var file in sourceFilesRel)
            {
                token.ThrowIfCancellationRequested();
                if (!targetFilesRel.Contains(file))
                {
                    missingInTargetFiles.Add(file);
                    AddPathToTree(sourceRoot, System.IO.Path.Combine(sourcePath, file), sourcePath, "missing");
                }
                currentStep++; UpdateProgress(file);
            }

            foreach (var file in targetFilesRel)
            {
                token.ThrowIfCancellationRequested();
                if (!sourceFilesRel.Contains(file))
                {
                    missingInSourceFiles.Add(file);
                    AddPathToTree(targetRoot, System.IO.Path.Combine(targetPath, file), targetPath, "added");
                }
                currentStep++; UpdateProgress(file);
            }

            // 3. Porovnání obsahu společných souborů
            foreach (var file in commonFiles)
            {
                token.ThrowIfCancellationRequested();
                var fullSourcePath = System.IO.Path.Combine(sourcePath, file);
                var fullTargetPath = System.IO.Path.Combine(targetPath, file);

                currentStep++; UpdateProgress(file); // Update před začátkem I/O čtení

                bool areEqual = await FilesAreEqualAsync(fullSourcePath, fullTargetPath, token);

                if (!areEqual)
                {
                    differentFiles.Add(file);
                    AddPathToTree(sourceRoot, fullSourcePath, sourcePath, "modified");
                    AddPathToTree(targetRoot, fullTargetPath, targetPath, "modified");
                }
            }

            reportProgressText("Generuji výstupy..."); // Strings.GeneratingOutputs

            // 4. Vygenerování textových výstupů
            var sbSource = new StringBuilder();
            var sbTarget = new StringBuilder();

            bool hasDifferences = missingInTargetDirs.Any() || missingInSourceDirs.Any() ||
                                  missingInTargetFiles.Any() || missingInSourceFiles.Any() ||
                                  differentFiles.Any();

            if (!hasDifferences)
            {
                const string message = "Složky jsou zcela identické (100% shoda)."; // Strings.FoldersIdentical
                sbSource.AppendLine(message);
                sbTarget.AppendLine(message);
                sourceRoot.Children.Add(new FileSystemItem { Name = message });
                targetRoot.Children.Add(new FileSystemItem { Name = message });
            }
            else
            {
                if (missingInTargetDirs.Any()) { sbSource.AppendLine("Chybějící složky v cíli:"); missingInTargetDirs.ForEach(d => sbSource.AppendLine(d)); sbSource.AppendLine(); }
                if (missingInTargetFiles.Any()) { sbSource.AppendLine("Chybějící soubory v cíli:"); missingInTargetFiles.ForEach(f => sbSource.AppendLine(f)); sbSource.AppendLine(); }
                if (differentFiles.Any()) { sbSource.AppendLine("Rozdílné soubory (ve zdroji):"); differentFiles.ForEach(f => sbSource.AppendLine(f)); sbSource.AppendLine(); }

                if (missingInSourceDirs.Any()) { sbTarget.AppendLine("Chybějící složky ve zdroji:"); missingInSourceDirs.ForEach(d => sbTarget.AppendLine(d)); sbTarget.AppendLine(); }
                if (missingInSourceFiles.Any()) { sbTarget.AppendLine("Chybějící soubory ve zdroji:"); missingInSourceFiles.ForEach(f => sbTarget.AppendLine(f)); sbTarget.AppendLine(); }
                if (differentFiles.Any()) { sbTarget.AppendLine("Rozdílné soubory (v cíli):"); differentFiles.ForEach(f => sbTarget.AppendLine(f)); sbTarget.AppendLine(); }
            }

            // 5. Jednorázový update UI na závěr
            await Dispatcher.InvokeAsync(() =>
            {
                TextBoxSourceOutput.Text = sbSource.ToString();
                TextBoxTargetOutput.Text = sbTarget.ToString();

                TreeViewSourceOutput.Items.Add(CreateTreeViewItem(sourceRoot));
                TreeViewTargetOutput.Items.Add(CreateTreeViewItem(targetRoot));

                ProgressBarComparison.Value = 100;
                TextBlockCurrentOperation.Text = "Dokončeno."; // Strings.Completed
            });
        }

        // --- POMOCNÉ METODY ---

        /// <summary>
        /// Manuální iterace adresářů řeší zásek Directory.EnumerateFiles na velkých discích.
        /// Lze ji plynule zrušit a reportovat aktuální stav.
        /// </summary>
        private void ScanDirectory(string rootPath, HashSet<string> dirsRel, HashSet<string> filesRel, CancellationToken token, Action<string> reportProgress)
        {
            var queue = new Queue<string>();
            queue.Enqueue(rootPath);

            var enumOptions = new EnumerationOptions { IgnoreInaccessible = true, ReturnSpecialDirectories = false };

            while (queue.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                string currentDir = queue.Dequeue();

                reportProgress(currentDir);

                try
                {
                    // Získáme pouze složky v aktuálním adresáři
                    foreach (var dir in Directory.EnumerateDirectories(currentDir, "*", enumOptions))
                    {
                        token.ThrowIfCancellationRequested();
                        dirsRel.Add(Path.GetRelativePath(rootPath, dir));
                        queue.Enqueue(dir); // Přidáme do fronty k prozkoumání
                    }

                    // Získáme pouze soubory v aktuálním adresáři
                    foreach (var file in Directory.EnumerateFiles(currentDir, "*", enumOptions))
                    {
                        token.ThrowIfCancellationRequested();
                        filesRel.Add(Path.GetRelativePath(rootPath, file));
                    }
                }
                catch (UnauthorizedAccessException) { /* Bezpečně ignorujeme cesty bez práv */ }
                catch (Exception) { /* Ignorujeme ostatní I/O chyby (dlouhé cesty atd.) */ }
            }
        }

        private async Task<bool> FilesAreEqualAsync(string file1, string file2, CancellationToken token)
        {
            var fi1 = new FileInfo(file1);
            var fi2 = new FileInfo(file2);

            if (fi1.Length != fi2.Length)
                return false;

            const int bufferSize = 1024 * 1024; // 1 MB buffer

            using var fs1 = new FileStream(file1, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize, FileOptions.Asynchronous);
            using var fs2 = new FileStream(file2, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize, FileOptions.Asynchronous);

            byte[] buffer1 = ArrayPool<byte>.Shared.Rent(bufferSize);
            byte[] buffer2 = ArrayPool<byte>.Shared.Rent(bufferSize);

            try
            {
                int bytesRead1;
                while ((bytesRead1 = await fs1.ReadAsync(buffer1, token)) > 0)
                {
                    int bytesRead2 = await fs2.ReadAsync(buffer2, 0, bytesRead1, token);

                    if (bytesRead1 != bytesRead2)
                        return false;

                    if (!buffer1.AsSpan(0, bytesRead1).SequenceEqual(buffer2.AsSpan(0, bytesRead1)))
                    {
                        return false;
                    }
                }
                return true;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer1);
                ArrayPool<byte>.Shared.Return(buffer2);
            }
        }

        private void AddPathToTree(FileSystemItem root, string fullPath, string rootPath, string status)
        {
            var relative = System.IO.Path.GetRelativePath(rootPath, fullPath);
            var parts = relative.Split(System.IO.Path.DirectorySeparatorChar);
            var current = root;
            string currentPath = rootPath;

            foreach (var part in parts)
            {
                currentPath = System.IO.Path.Combine(currentPath, part);
                var child = current.Children.FirstOrDefault(c => c.Name == part);
                if (child == null)
                {
                    child = new FileSystemItem
                    {
                        Name = part,
                        FullPath = currentPath
                    };
                    current.Children.Add(child);
                }
                current = child;
            }

            current.Status = status;
        }

        private TreeViewItem CreateTreeViewItem(FileSystemItem item)
        {
            var treeViewItem = new TreeViewItem
            {
                Header = item.Name,
                Tag = item.FullPath
            };

            switch (item.Status)
            {
                case "missing":
                    treeViewItem.Foreground = Brushes.Red;
                    break;
                case "added":
                    treeViewItem.Foreground = Brushes.Green;
                    break;
                case "modified":
                    treeViewItem.Foreground = Brushes.Orange;
                    break;
                default:
                    treeViewItem.Foreground = Brushes.Black;
                    break;
            }

            foreach (var child in item.Children)
            {
                treeViewItem.Items.Add(CreateTreeViewItem(child));
            }

            return treeViewItem;
        }

        private void TextBoxTargetOutput_TextChanged(object sender, TextChangedEventArgs e)
        {
            // Prostor pro logiku při změně textu
        }
    }
}