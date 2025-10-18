//! \file       GarExtract.cs
//! \date       Fri Jul 25 05:52:27 2014
//! \brief      Extract archive frontend.
//
// Copyright (C) 2014-2017 by morkt
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to
// deal in the Software without restriction, including without limitation the
// rights to use, copy, modify, merge, publish, distribute, sublicense, and/or
// sell copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS
// IN THE SOFTWARE.
//

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Forms.VisualStyles;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using GameRes;
using GARbro.GUI;
using GARbro.GUI.Properties;
using GARbro.GUI.Strings;
using static System.Windows.Forms.VisualStyles.VisualStyleElement.ProgressBar;

namespace GARbro.GUI
{
    public partial class MainWindow : Window
    {
        /// <summary>
        /// Handle "Extract item" command.
        /// </summary>
        private void ExtractItemExec(object sender, ExecutedRoutedEventArgs e)
        {
            var entries = CurrentDirectory.SelectedItems;
            if (entries.Count == 0 && !ViewModel.IsArchive) //物理ディレクトリ内で何も選択せずに実行 ⇒ 何もしない
            {
                SetStatusText(guiStrings.MsgChooseFiles);
                return;
            }

            GarExtract extractor = null;
            try
            {
                string destination = Settings.Default.appLastDestination;
                if (!Directory.Exists(destination))
                    destination = "";
                var vm = ViewModel;
                if (vm.IsArchive)
                {
                    if (string.IsNullOrEmpty(destination))
                        destination = Path.GetDirectoryName(vm.Path.First());
                    var archive_name = vm.Path[vm.Path.Count - 2];
                    extractor = new GarExtract(this, archive_name, VFS.Top as ArchiveFileSystem);
                    if (entries.Count == 0) //アーカイブ内で何も選択せずに実行 ⇒ 全ファイル出力
                        extractor.ExtractAll(destination);
                    else
                        extractor.Extract(entries[0] as EntryViewModel, destination);
                }
                else if (!(entries[0] as EntryViewModel).IsDirectory)
                {
                    List<string> source_list = new List<string>();
                    foreach (var ent in entries)
                        source_list.Add((ent as EntryViewModel).Source.Name);

                    SetBusyState();

                    if (string.IsNullOrEmpty(destination))
                    {
                        // extract into directory named after archive
                        if (!string.IsNullOrEmpty(Path.GetExtension((entries[0] as EntryViewModel).Name)))
                            destination = Path.GetFileNameWithoutExtension(source_list[0]);
                        else
                            destination = vm.Path.First();
                    }
                    extractor = new GarExtract(this, source_list);
                    extractor.ExtractAll(destination);
                }
            }
            catch (OperationCanceledException X)
            {
                SetStatusText(X.Message);
            }
            catch (Exception X)
            {
                Console.WriteLine(X.Message);
                Console.WriteLine(X.StackTrace);
                PopupError(X.Message, guiStrings.MsgErrorExtracting);
            }
            finally
            {
                if (null != extractor && !extractor.IsActive)
                    extractor.Dispose();
            }
        }
    }

    sealed internal class GarExtract : GarOperation, IDisposable
    {
        private string m_arc_name;
        private ArchiveFileSystem m_fs;
        private readonly bool m_should_ascend;
        private bool m_skip_images = false;
        private bool m_skip_script = false;
        private bool m_skip_audio = false;
        private bool m_adjust_image_offset = false;
        private bool m_convert_audio;
        private ImageFormat m_image_format;
        private int m_extract_count;
        private int m_skip_count;
        private bool m_extract_in_progress = false;
        private readonly IEnumerable<Entry> m_file_list = Enumerable.Empty<Entry>(); //出力ファイルのリスト
        private string m_destination; //出力先ディレクトリ


        public bool IsActive { get { return m_extract_in_progress; } }

        public GarExtract(MainWindow parent, List<string> source_list) : base(parent, guiStrings.TextExtractionError)
        {
            m_arc_name = Path.GetFileName(source_list.First());
            if (source_list.Count >= 2)
                m_arc_name += string.Format(guiStrings.MsgExtractArchive, source_list.Count - 1);

            m_should_ascend = true;

            foreach (string source in source_list)
            {
                VFS.FullPath = new string[] { Path.GetDirectoryName(source) };
                var arc = ArcFile.TryOpen(source);
                foreach (var entry in arc.Dir)
                    entry.Arc = arc;
                m_file_list = Enumerable.Concat(m_file_list, arc.Dir);
            }
        }

        public GarExtract(MainWindow parent, string source, ArchiveFileSystem fs) : base(parent, guiStrings.TextExtractionError)
        {
            if (null == fs)
                throw new UnknownFormatException();
            m_fs = fs;
            m_arc_name = Path.GetFileName(source);
            m_should_ascend = false;
            m_file_list = m_fs.GetFilesRecursive();
            foreach (var entry in m_file_list)
                entry.Arc = m_fs.Source;
        }

        private void PrepareDestination(string destination)
        {
            bool stop_watch = !m_main.ViewModel.IsArchive;
            if (stop_watch)
                m_main.StopWatchDirectoryChanges();
            try
            {
                Directory.CreateDirectory(destination);
                Directory.SetCurrentDirectory(destination);
                Settings.Default.appLastDestination = destination;
            }
            finally
            {
                if (stop_watch)
                    m_main.ResumeWatchDirectoryChanges();
            }
        }

        public void ExtractAll(string destination)
        {
            if (!m_file_list.Any())
            {
                m_main.SetStatusText(string.Format("{1}: {0}", guiStrings.MsgEmptyArchive, m_arc_name));
                return;
            }
            var extractDialog = new ExtractArchiveDialog(m_arc_name, destination);
            extractDialog.Owner = m_main;
            var result = extractDialog.ShowDialog();
            if (!result.Value)
                return;

            m_destination = extractDialog.Destination;
            if (!string.IsNullOrEmpty(m_destination))
                m_destination = Path.GetFullPath(m_destination);
            m_skip_images = !extractDialog.ExtractImages.IsChecked.Value;
            m_skip_script = !extractDialog.ExtractText.IsChecked.Value;
            m_skip_audio = !extractDialog.ExtractAudio.IsChecked.Value;
            if (!m_skip_images)
                m_image_format = extractDialog.GetImageFormat(extractDialog.ImageConversionFormat);

            m_main.SetStatusText(string.Format(guiStrings.MsgExtractingTo, m_arc_name, destination));
            ExtractFilesFromArchive(string.Format(guiStrings.MsgExtractingArchive, m_arc_name), m_file_list);
        }

        public void Extract(EntryViewModel entry, string destination)
        {
            var view_model = m_main.ViewModel;
            var selected = m_main.CurrentDirectory.SelectedItems.Cast<EntryViewModel>();
            if (!selected.Any())
                selected = view_model;

            IEnumerable<Entry> file_list = selected.Select(e => e.Source);
            if (m_fs is TreeArchiveFileSystem)
                file_list = (m_fs as TreeArchiveFileSystem).GetFilesRecursive(file_list);

            if (!file_list.Any())
            {
                m_main.SetStatusText(guiStrings.MsgChooseFiles);
                return;
            }

            ExtractDialog extractDialog;
            bool multiple_files = file_list.Skip(1).Any();
            if (multiple_files)
                extractDialog = new ExtractArchiveDialog(m_arc_name, destination);
            else
                extractDialog = new ExtractFile(entry, destination);
            extractDialog.Owner = m_main;
            var result = extractDialog.ShowDialog();
            if (!result.Value)
                return;
            if (multiple_files)
            {
                m_skip_images = !Settings.Default.appExtractImages;
                m_skip_script = !Settings.Default.appExtractText;
                m_skip_audio = !Settings.Default.appExtractAudio;
            }
            m_destination = extractDialog.Destination;
            if (!string.IsNullOrEmpty(m_destination))
                m_destination = Path.GetFullPath(m_destination);
            if (!m_skip_images)
                m_image_format = FormatCatalog.Instance.ImageFormats.FirstOrDefault(f => f.Tag.Equals(Settings.Default.appImageFormat));

            foreach (var file in file_list)
                file.Arc = m_fs.Source;

            ExtractFilesFromArchive(string.Format(guiStrings.MsgExtractingFile, m_arc_name), file_list);
        }

        private void ExtractFilesFromArchive(string text, IEnumerable<Entry> file_list)
        {
            file_list = file_list.Where(e => e.Offset >= 0);
            if (file_list.Skip(1).Any() // file_list.Count() > 1
                && (m_skip_images || m_skip_script || m_skip_audio))
                file_list = file_list.Where(f => !(m_skip_images && f.Type == "image") &&
                                                  !(m_skip_script && f.Type == "script") &&
                                                  !(m_skip_audio && f.Type == "audio"));
            if (!file_list.Any())
            {
                m_main.SetStatusText(string.Format("{1}: {0}", guiStrings.MsgNoFiles, m_arc_name));
                return;
            }
            file_list = file_list.OrderBy(e => e.Offset);
            m_progress_dialog = new ProgressDialog()
            {
                WindowTitle = guiStrings.TextTitle,
                Text = text,
                Description = "",
                MinimizeBox = true,
            };
            if (!file_list.Skip(1).Any()) // 1 == file_list.Count()
            {
                m_progress_dialog.Description = file_list.First().Name;
                m_progress_dialog.ProgressBarStyle = ProgressBarStyle.MarqueeProgressBar;
            }
            m_convert_audio = !m_skip_audio && Settings.Default.appConvertAudio;
            m_progress_dialog.DoWork += (s, e) => ExtractWorker(file_list); //Extract処理の埋め込み
            m_progress_dialog.RunWorkerCompleted += OnExtractComplete; //Extract完了後処理の埋め込み
            m_main.IsEnabled = false; //Extract中はメインウィンドウ非アクティブ
            m_progress_dialog.ShowDialog(m_main); //進捗ダイアログ表示とともにExtract処理開始
            m_extract_in_progress = true; //これは重要消しちゃダメ
        }

        void ExtractWorker(IEnumerable<Entry> file_list)
        {
            m_extract_count = 0;
            m_skip_count = 0;
            int total = file_list.Count();
            int progress_count = 0;
            bool ignore_errors = false;
            foreach (var entry in file_list)
            {
                if (m_progress_dialog.CancellationPending)
                    break;
                if (total > 1)
                    m_progress_dialog.ReportProgress(progress_count++ * 100 / total, null, entry.Name);
                try
                {
                    if (null != m_image_format && entry.Type == "image")
                        ExtractImage(entry, m_image_format);
                    else if (m_convert_audio && entry.Type == "audio")
                        ExtractAudio(entry);
                    else
                        ExtractEntryAsIs(entry);
                    ++m_extract_count;
                }
                catch (SkipExistingFileException)
                {
                    ++m_skip_count;
                    continue;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception X)
                {
                    Console.WriteLine(X.Message);
                    Console.WriteLine(X.StackTrace);
                    if (!ignore_errors)
                    {
                        var error_text = string.Format(guiStrings.TextErrorExtracting, entry.Name, X.Message);
                        var result = ShowErrorDialog(error_text);
                        if (!result.Continue)
                            break;
                        ignore_errors = result.IgnoreErrors;
                    }
                    ++m_skip_count;
                }
            }
        }

        void ExtractEntryAsIs(Entry entry)
        {
            using (var input = entry.Arc.OpenEntry(entry))
            {
                PrepareDestination(m_destination);
                using (var output = CreateNewFile(entry.Name, true))
                    input.CopyTo(output);
            }
        }

        void ExtractImage(Entry entry, ImageFormat target_format)
        {
            using (var decoder = entry.Arc.OpenImage(entry))
            {
                PrepareDestination(m_destination);
                var src_format = decoder.SourceFormat; // could be null
                string target_ext = target_format.Extensions.FirstOrDefault() ?? "";
                string outname = Path.ChangeExtension(entry.Name, target_ext);
                if (src_format == target_format)
                {
                    // source format is the same as a target, copy file as is
                    using (var output = CreateNewFile(outname, true))
                        decoder.Source.CopyTo(output);
                    return;
                }
                ImageData image = decoder.Image;
                if (m_adjust_image_offset)
                {
                    image = AdjustImageOffset(image);
                }
                using (var outfile = CreateNewFile(outname, true))
                {
                    target_format.Write(outfile, image);
                }
            }
        }

        static ImageData AdjustImageOffset(ImageData image)
        {
            if (0 == image.OffsetX && 0 == image.OffsetY)
                return image;
            int width = (int)image.Width + image.OffsetX;
            int height = (int)image.Height + image.OffsetY;
            if (width <= 0 || height <= 0)
                return image;

            int x = Math.Max(image.OffsetX, 0);
            int y = Math.Max(image.OffsetY, 0);
            int src_x = image.OffsetX < 0 ? Math.Abs(image.OffsetX) : 0;
            int src_y = image.OffsetY < 0 ? Math.Abs(image.OffsetY) : 0;
            int src_stride = (int)image.Width * (image.BPP + 7) / 8;
            int dst_stride = width * (image.BPP + 7) / 8;
            var pixels = new byte[height * dst_stride];
            int offset = y * dst_stride + x * image.BPP / 8;
            Int32Rect rect = new Int32Rect(src_x, src_y, (int)image.Width - src_x, 1);
            for (int row = src_y; row < image.Height; ++row)
            {
                rect.Y = row;
                image.Bitmap.CopyPixels(rect, pixels, src_stride, offset);
                offset += dst_stride;
            }
            var bitmap = BitmapSource.Create(width, height, image.Bitmap.DpiX, image.Bitmap.DpiY,
                image.Bitmap.Format, image.Bitmap.Palette, pixels, dst_stride);
            return new ImageData(bitmap);
        }

        void ExtractAudio(Entry entry)
        {
            using (var file = entry.Arc.OpenBinaryEntry(entry))
            {
                using (var sound = AudioFormat.Read(file))
                {
                    PrepareDestination(m_destination);
                    if (null == sound)
                        throw new InvalidFormatException(guiStrings.MsgUnableInterpretAudio);
                    ConvertAudio(entry.Name, sound);
                }
            }
        }

        public void ConvertAudio(string filename, SoundInput input)
        {
            string source_format = input.SourceFormat;
            if (GarConvertMedia.CommonAudioFormats.Contains(source_format))
            {
                var output_name = Path.ChangeExtension(filename, source_format);
                using (var output = CreateNewFile(output_name, true))
                {
                    input.Source.Position = 0;
                    input.Source.CopyTo(output);
                }
            }
            else
            {
                var output_name = Path.ChangeExtension(filename, "wav");
                using (var output = CreateNewFile(output_name, true))
                    AudioFormat.Wav.Write(input, output);
            }
        }

        void OnExtractComplete(object sender, RunWorkerCompletedEventArgs e)
        {
            m_main.IsEnabled = true;
            m_extract_in_progress = false;
            m_progress_dialog.Dispose();
            ArcDispose();
            m_main.Activate();
            m_main.ListViewFocus();
            if (!m_main.ViewModel.IsArchive)
            {
                m_main.Dispatcher.Invoke(m_main.RefreshView);
            }
            m_main.SetStatusText(Localization.Format("MsgExtractedFiles", m_extract_count));
            this.Dispose();
        }

        private void ArcDispose()
        {
            if (m_should_ascend)
                foreach (var arc in m_file_list.ToList().ConvertAll(e => e.Arc).Distinct())
                    arc.Dispose();
        }

        #region IDisposable Members
        bool disposed = false;

        public void Dispose()
        {
            if (!disposed)
            {
                if (m_should_ascend)
                {
                    VFS.ChDir("..");
                }
                disposed = true;
            }
            GC.SuppressFinalize(this);
        }
        #endregion
    }
}
