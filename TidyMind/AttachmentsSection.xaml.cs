using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace TidyMind
{
    // Project Details' "Files": a drop zone (or Browse) that copies files in, and the attached files as rows that
    // open on a click. Works on its own list; the dialog writes Attachments back to the project when it closes,
    // like its other fields, and the copies of what was removed are deleted once that save has gone through.
    public partial class AttachmentsSection : UserControl
    {
        private const int ThumbnailWidth = 120; // decoded small: a long list of photos stays quick

        private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif" };

        // A file waiting to be copied in or being copied: shown as a row with its progress until it's done.
        private class PendingCopy
        {
            public string Path;
            public string Name;
            public double Progress;
            public ProgressBar Bar; // the row currently on screen
        }

        private readonly List<Attachment> attachments = new List<Attachment>();
        private readonly List<Attachment> removed = new List<Attachment>();
        private readonly List<PendingCopy> pending = new List<PendingCopy>();
        private readonly CancellationTokenSource copying = new CancellationTokenSource();
        private bool closed;

        public AttachmentsSection()
        {
            InitializeComponent();
        }

        public List<Attachment> Attachments => attachments.ToList();

        // Taken out in this session: their copies are deleted once the project has been saved without them.
        public IReadOnlyList<Attachment> Removed => removed;

        public bool IsCopying => pending.Count > 0;

        public void Load(IEnumerable<Attachment> existing)
        {
            attachments.AddRange(existing ?? Enumerable.Empty<Attachment>());
            Render();
        }

        // The dialog is closing: copies still running are stopped, and one that finishes anyway is deleted rather
        // than left behind with nothing pointing at it.
        public void Close()
        {
            closed = true;
            copying.Cancel();
        }

        // ---- Adding -------------------------------------------------------

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dialog = new OpenFileDialog { Multiselect = true, Filter = "All files (*.*)|*.*", Title = "Attach Files" };
            if (dialog.ShowDialog(Window.GetWindow(this)) == true)
                AddFiles(dialog.FileNames);
        }

        private static bool HasFiles(DragEventArgs e) => e.Data.GetDataPresent(DataFormats.FileDrop);

        private void DropZone_DragEnter(object sender, DragEventArgs e) => SetDropHighlight(HasFiles(e));

        private void DropZone_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        // Also raised when the pointer moves onto the Browse button inside the zone; only leaving the zone counts.
        private void DropZone_DragLeave(object sender, DragEventArgs e)
        {
            Point at = e.GetPosition(DropZone);
            if (at.X <= 0 || at.Y <= 0 || at.X >= DropZone.ActualWidth || at.Y >= DropZone.ActualHeight)
                SetDropHighlight(false);
        }

        private void DropZone_Drop(object sender, DragEventArgs e)
        {
            SetDropHighlight(false);
            if (HasFiles(e))
                AddFiles((string[])e.Data.GetData(DataFormats.FileDrop));
        }

        private void SetDropHighlight(bool on)
        {
            DropZone.BorderBrush = (Brush)FindResource(on ? "Accent" : "Border");
            DropZone.Background = (Brush)FindResource(on ? "AccentSoft" : "BgPanel");
        }

        private async void AddFiles(IReadOnlyCollection<string> paths)
        {
            List<string> files = paths.Where(File.Exists).ToList();
            if (files.Count < paths.Count)
                MessageBox.Show(Window.GetWindow(this), "Folders can't be attached, only files." +
                        (files.Count > 0 ? " The files were added." : ""),
                    "Attach Files", MessageBoxButton.OK, MessageBoxImage.Information);

            List<PendingCopy> queue = new List<PendingCopy>();
            foreach (string path in files)
            {
                if (!ConfirmIfLarge(path))
                    continue;
                PendingCopy copy = new PendingCopy { Path = path, Name = Path.GetFileName(path) };
                pending.Add(copy);
                queue.Add(copy);
            }
            if (queue.Count == 0)
                return;
            Render();

            // One at a time, in the order given: the list fills in that order, and big files don't fight over the disk.
            foreach (PendingCopy copy in queue)
                await AddFile(copy);
        }

        private bool ConfirmIfLarge(string path)
        {
            long size;
            try
            {
                size = new FileInfo(path).Length;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return true; // unreadable: the copy itself will say so
            }

            return size <= AttachmentStore.LargeFileBytes
                || MessageBox.Show(Window.GetWindow(this),
                       "'" + Path.GetFileName(path) + "' is " + FormatSize(size) + ". Copying it takes a while and uses that " +
                       "much disk space. Attach it anyway?", "Large File",
                       MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        }

        // Copies one file in off the UI thread, its row showing the progress meanwhile.
        private async Task AddFile(PendingCopy copy)
        {
            Attachment attachment = null;
            bool stopped = false;
            try
            {
                Progress<double> progress = new Progress<double>(done =>
                {
                    copy.Progress = done;
                    if (copy.Bar != null)
                        copy.Bar.Value = done;
                });
                attachment = await AttachmentStore.ImportAsync(copy.Path, progress, copying.Token);
            }
            catch (OperationCanceledException)
            {
                stopped = true;
            }
            finally
            {
                pending.Remove(copy);
            }

            if (closed)
            {
                if (attachment != null)
                    AttachmentStore.Delete(new[] { attachment });
                return;
            }

            if (attachment != null)
                attachments.Add(attachment);
            else if (!stopped)
                MessageBox.Show(Window.GetWindow(this), "Couldn't add '" + copy.Name + "': it couldn't be read or copied.",
                    "Couldn't Add File", MessageBoxButton.OK, MessageBoxImage.Warning);
            Render();
        }

        // ---- The list -----------------------------------------------------

        private void Render()
        {
            FileRows.Children.Clear();
            foreach (Attachment attachment in attachments)
                FileRows.Children.Add(FileRow(attachment));
            foreach (PendingCopy copy in pending)
                FileRows.Children.Add(PendingRow(copy));

            FileScroll.Visibility = FileRows.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private FrameworkElement FileRow(Attachment attachment)
        {
            Button remove = new Button
            {
                Style = (Style)FindResource("RowDeleteStyle"),
                ToolTip = "Remove",
                Visibility = Visibility.Hidden,
                VerticalAlignment = VerticalAlignment.Center
            };
            remove.Click += (s, e) => Remove(attachment);
            Grid.SetColumn(remove, 2);

            FrameworkElement text = RowText(attachment.DisplayName,
                FormatSize(attachment.SizeBytes) + "  ·  " + attachment.AddedDate.ToString("MMM d, yyyy", CultureInfo.InvariantCulture));
            Grid.SetColumn(text, 1);

            Border row = RowFrame(new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = GridLength.Auto },
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = GridLength.Auto }
                },
                Children = { Thumbnail(attachment), text, remove }
            });
            row.Cursor = Cursors.Hand;
            row.ToolTip = "Open " + attachment.DisplayName;

            Brush hover = (Brush)FindResource("BgSubtle");
            row.MouseEnter += (s, e) =>
            {
                row.Background = hover;
                remove.Visibility = Visibility.Visible;
            };
            row.MouseLeave += (s, e) =>
            {
                row.Background = Brushes.Transparent;
                remove.Visibility = Visibility.Hidden;
            };

            CardEffects.AttachClick(row, () => Open(attachment)); // the × handles its own press, so it doesn't open
            row.ContextMenu = Menu(attachment);
            return row;
        }

        private FrameworkElement PendingRow(PendingCopy copy)
        {
            copy.Bar = new ProgressBar { Height = 3, Minimum = 0, Maximum = 1, Value = copy.Progress, Margin = new Thickness(0, 6, 8, 0) };

            StackPanel text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock
            {
                Text = copy.Name,
                FontSize = 13.5,
                Foreground = (Brush)FindResource("TextSub"),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            text.Children.Add(copy.Bar);
            Grid.SetColumn(text, 1);

            return RowFrame(new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = GridLength.Auto },
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
                },
                Children = { TypeIcon("\uE896", "#8A8A87"), text } // Download: "on its way in"
            });
        }

        private static Border RowFrame(Grid content)
        {
            return new Border
            {
                Child = content,
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6, 5, 4, 5),
                MinHeight = 50
            };
        }

        // Name (cut short with an ellipsis; the row's tooltip has it in full) over size and date.
        private FrameworkElement RowText(string name, string details)
        {
            StackPanel text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            text.Children.Add(new TextBlock
            {
                Text = name,
                FontSize = 13.5,
                Foreground = (Brush)FindResource("TextMain"),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            text.Children.Add(new TextBlock
            {
                Text = details,
                FontSize = 11.5,
                Foreground = (Brush)FindResource("TextSub"),
                Margin = new Thickness(0, 1, 0, 0)
            });
            return text;
        }

        // ---- Icons and thumbnails ------------------------------------------

        // An image shows itself (decoded small, off the UI thread the first time); anything else an icon by type.
        private FrameworkElement Thumbnail(Attachment attachment)
        {
            string extension = Path.GetExtension(attachment.StoredFileName ?? "").ToLowerInvariant();
            if (!ImageExtensions.Contains(extension) || !AttachmentStore.Exists(attachment))
                return TypeIcon(extension);

            Border frame = new Border
            {
                Width = 38,
                Height = 38,
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 0, 12, 0),
                Background = (Brush)FindResource("BgSubtle")
            };
            string path = AttachmentStore.PathOf(attachment.StoredFileName);
            ImageSource loaded = ImageCache.TryGet(path);
            if (loaded != null)
                frame.Background = new ImageBrush(loaded) { Stretch = Stretch.UniformToFill };
            else
                ShowWhenDecoded(frame, ImageCache.LoadAsync(path, ThumbnailWidth), extension);
            return frame;
        }

        // Unreadable as an image (e.g. no WebP codec on this PC): the plain icon instead.
        private async void ShowWhenDecoded(Border frame, Task<System.Windows.Media.Imaging.BitmapImage> decoding, string extension)
        {
            ImageSource image = await decoding;
            if (image != null)
                frame.Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
            else
                frame.Child = TypeIcon(extension, inFrame: true);
        }

        private FrameworkElement TypeIcon(string extension, bool inFrame = false)
        {
            var (glyph, color) = IconFor(extension);
            return TypeIcon(glyph, color, inFrame);
        }

        // A Segoe MDL2 glyph in the type's color, on a faint tint of it.
        private static FrameworkElement TypeIcon(string glyph, string hex, bool inFrame = false)
        {
            Color color = (Color)ColorConverter.ConvertFromString(hex);
            return new Border
            {
                Width = 38,
                Height = 38,
                CornerRadius = new CornerRadius(6),
                Margin = inFrame ? new Thickness(0) : new Thickness(0, 0, 12, 0),
                Background = new SolidColorBrush(Color.FromArgb(0x1F, color.R, color.G, color.B)),
                Child = new TextBlock
                {
                    Text = glyph,
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 18,
                    Foreground = new SolidColorBrush(color),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
        }

        // Segoe MDL2 Assets has no Office logos, so each kind gets a fitting symbol in the color people know it by.
        private static (string Glyph, string Hex) IconFor(string extension)
        {
            switch (extension)
            {
                case ".pdf":
                    return ("\uEA90", "#D93F3F"); // PDF
                case ".doc": case ".docx": case ".odt": case ".rtf":
                    return ("\uE8A5", "#2B579A"); // Document
                case ".xls": case ".xlsx": case ".ods": case ".csv":
                    return ("\uE80A", "#217346"); // ViewAll (a grid)
                case ".ppt": case ".pptx": case ".odp":
                    return ("\uE786", "#C43E1C"); // Slideshow
                case ".zip": case ".rar": case ".7z": case ".tar": case ".gz":
                    return ("\uF012", "#8A6D3B"); // ZipFolder
                case ".jpg": case ".jpeg": case ".png": case ".webp": case ".bmp": case ".gif":
                    return ("\uEB9F", "#6B6965"); // Photo (an image whose file is gone or unreadable)
                default:
                    return ("\uE7C3", "#6B6965"); // Page
            }
        }

        // ---- Actions ------------------------------------------------------

        private ContextMenu Menu(Attachment attachment)
        {
            MenuItem open = new MenuItem { Header = "Open" };
            open.Click += (s, e) => Open(attachment);
            MenuItem show = new MenuItem { Header = "Show in folder" };
            show.Click += (s, e) => ShowInFolder(attachment);
            MenuItem saveCopy = new MenuItem { Header = "Save a copy as..." };
            saveCopy.Click += (s, e) => SaveCopy(attachment);
            MenuItem rename = new MenuItem { Header = "Rename" };
            rename.Click += (s, e) => Rename(attachment);
            MenuItem remove = new MenuItem { Header = "Remove" };
            remove.Click += (s, e) => Remove(attachment);

            return new ContextMenu { Items = { open, show, saveCopy, rename, new Separator(), remove } };
        }

        // False (after offering to drop the entry) when the stored copy has gone missing.
        private bool StillOnDisk(Attachment attachment)
        {
            if (AttachmentStore.Exists(attachment))
                return true;

            if (MessageBox.Show(Window.GetWindow(this),
                    "This file is no longer on disk. Remove '" + attachment.DisplayName + "' from the project?",
                    "File Missing", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                RemoveEntry(attachment);
            return false;
        }

        private void Open(Attachment attachment)
        {
            if (!StillOnDisk(attachment)) return;
            try
            {
                Process.Start(new ProcessStartInfo(AttachmentStore.PathOf(attachment.StoredFileName)) { UseShellExecute = true });
            }
            catch (Win32Exception) // e.g. no program is set up to open this kind of file
            {
                MessageBox.Show(Window.GetWindow(this), "Couldn't open '" + attachment.DisplayName +
                        "': Windows has no program set up for this kind of file.",
                    "Couldn't Open", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ShowInFolder(Attachment attachment)
        {
            if (!StillOnDisk(attachment)) return;
            Process.Start("explorer.exe", "/select,\"" + AttachmentStore.PathOf(attachment.StoredFileName) + "\"");
        }

        private void SaveCopy(Attachment attachment)
        {
            if (!StillOnDisk(attachment)) return;

            SaveFileDialog dialog = new SaveFileDialog { FileName = attachment.DisplayName, Filter = "All files (*.*)|*.*" };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            try
            {
                File.Copy(AttachmentStore.PathOf(attachment.StoredFileName), dialog.FileName, overwrite: true);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                MessageBox.Show(Window.GetWindow(this), "Couldn't save the copy: " + e.Message,
                    "Couldn't Save", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // Only the name shown here changes; the stored copy keeps its own name (and extension, which picks its icon).
        private void Rename(Attachment attachment)
        {
            string name = InputDialog.Prompt(this, "Rename File", "Enter a new name.", attachment.DisplayName, "Rename");
            if (string.IsNullOrWhiteSpace(name) || name == attachment.DisplayName) return;
            attachment.DisplayName = name.Trim();
            Render();
        }

        private void Remove(Attachment attachment)
        {
            if (MessageBox.Show(Window.GetWindow(this), "Remove '" + attachment.DisplayName + "' from this project?",
                    "Remove File", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                RemoveEntry(attachment);
        }

        private void RemoveEntry(Attachment attachment)
        {
            attachments.Remove(attachment);
            removed.Add(attachment);
            Render();
        }

        // The list scrolls itself while it has rows to show in that direction; at either end the wheel moves on to
        // the window (like the task list).
        private void FileScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Delta < 0 ? FileScroll.VerticalOffset < FileScroll.ScrollableHeight - 0.5 : FileScroll.VerticalOffset > 0.5)
                return;

            // Raised from this control, outside the list's own scroll area, so it bubbles straight to the window's.
            e.Handled = true;
            var forward = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent };
            RaiseEvent(forward);
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            double kb = bytes / 1024.0;
            if (kb < 1024) return kb.ToString("0", CultureInfo.InvariantCulture) + " KB";
            double mb = kb / 1024;
            if (mb < 1024) return mb.ToString(mb < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + " MB";
            return (mb / 1024).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
        }
    }
}
