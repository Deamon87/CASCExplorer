﻿using CASCExplorer.Properties;
using SereniaBLPLib;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CASCExplorer
{
    delegate void OnStorageChangedDelegate();
    delegate void OnCleanupDelegate();

    class CASCViewHelper
    {
        private ExtractProgress extractProgress;
        private CASCHandler _casc;
        private CASCFolder _root;
        private CASCEntrySorter Sorter = new CASCEntrySorter();
        private ScanForm scanForm;
        private NumberFormatInfo sizeNumberFmt = new NumberFormatInfo()
        {
            NumberGroupSizes = new int[] { 3, 3, 3, 3, 3 },
            NumberDecimalDigits = 0,
            NumberGroupSeparator = " "
        };

        public event OnStorageChangedDelegate OnStorageChanged;
        public event OnCleanupDelegate OnCleanup;

        public CASCHandler CASC
        {
            get { return _casc; }
        }

        public CASCFolder Root
        {
            get { return _root; }
        }

        public void ExtractFiles(NoFlickerListView filesList)
        {
            CASCFolder folder = filesList.Tag as CASCFolder;

            if (folder == null)
                return;

            if (!filesList.HasSelection)
                return;

            if (extractProgress == null)
                extractProgress = new ExtractProgress();

            var files = folder.GetFiles(filesList.SelectedIndices.Cast<int>()).ToList();
            extractProgress.SetExtractData(_casc, files);
            extractProgress.ShowDialog();
        }

        public async Task ExtractInstallFiles(Action<int> progressCallback)
        {
            if (_casc == null)
                return;

            IProgress<int> progress = new Progress<int>(progressCallback);

            await Task.Run(() =>
            {
                var installFiles = _casc.Install.GetEntries("Windows");
                var build = _casc.Config.BuildName;

                int numFiles = installFiles.Count();
                int numDone = 0;

                foreach (var file in installFiles)
                {
                    _casc.ExtractFile(_casc.Encoding.GetEntry(file.MD5).Key, "data\\" + build + "\\install_files", file.Name);

                    progress.Report((int)(++numDone / (float)numFiles * 100.0f));
                }
            });
        }

        public async Task AnalyzeUnknownFiles(Action<int> progressCallback)
        {
            if (_casc == null)
                return;

            IProgress<int> progress = new Progress<int>(progressCallback);

            await Task.Run(() =>
            {
                FileScanner scanner = new FileScanner(_casc, _root);

                Dictionary<int, string> idToName = new Dictionary<int, string>();

                if (_casc.Config.GameType == CASCGameType.WoW)
                {
                    if (_casc.FileExists("DBFilesClient\\SoundEntries.db2"))
                    {
                        using (Stream stream = _casc.OpenFile("DBFilesClient\\SoundEntries.db2"))
                        {
                            DB2Reader se = new DB2Reader(stream);

                            foreach (var row in se)
                            {
                                string name = row.Value.GetField<string>(2);

                                int type = row.Value.GetField<int>(1);

                                bool many = row.Value.GetField<int>(4) > 0;

                                for (int i = 3; i < 23; i++)
                                    idToName[row.Value.GetField<int>(i)] = "unknown\\sound\\" + name + (many ? "_" + (i - 2).ToString("D2") : "") + (type == 28 ? ".mp3" : ".ogg");
                            }
                        }
                    }

                    if (_casc.FileExists("DBFilesClient\\SoundKit.db2") && _casc.FileExists("DBFilesClient\\SoundKitEntry.db2"))
                    {
                        using (Stream skStream = _casc.OpenFile("DBFilesClient\\SoundKit.db2"))
                        using (Stream skeStream = _casc.OpenFile("DBFilesClient\\SoundKitEntry.db2"))
                        {
                            DB3Reader sk = new DB3Reader(skStream);
                            DB3Reader ske = new DB3Reader(skeStream);

                            Dictionary<int, List<int>> lookup = new Dictionary<int, List<int>>();

                            foreach (var row in ske)
                            {
                                int soundKitId = row.Value.GetField<ushort>(0xC);

                                if (!lookup.ContainsKey(soundKitId))
                                    lookup[soundKitId] = new List<int>();

                                lookup[soundKitId].Add(row.Value.GetField<int>(0x4));
                            }

                            foreach (var row in sk)
                            {
                                string name = row.Value.GetField<string>(0x4).Replace(':', '_');

                                int type = row.Value.GetField<byte>(0x2C);

                                List<int> ske_entries;

                                if (!lookup.TryGetValue(row.Key, out ske_entries))
                                    continue;

                                bool many = ske_entries.Count > 1;

                                int i = 0;

                                foreach (var fid in ske_entries)
                                {
                                    idToName[fid] = "unknown\\sound\\" + name + (many ? "_" + (i + 1).ToString("D2") : "") + (type == 28 ? ".mp3" : ".ogg");
                                    i++;
                                }
                            }
                        }
                    }
                }

                CASCFolder unknownFolder = _root.GetEntry("unknown") as CASCFolder;

                if (unknownFolder == null)
                    return;

                IEnumerable<CASCFile> files = unknownFolder.GetFiles(null, true);
                int numTotal = files.Count();
                int numDone = 0;

                foreach (var unknownEntry in files)
                {
                    CASCFile unknownFile = unknownEntry as CASCFile;

                    string name;
                    if (idToName.TryGetValue(_casc.Root.GetEntries(unknownFile.Hash).First().FileDataId, out name))
                        unknownFile.FullName = name;
                    else
                    {
                        string ext = scanner.GetFileExtension(unknownFile);
                        unknownFile.FullName += ext;
                    }

                    progress.Report((int)(++numDone / (float)numTotal * 100.0f));
                }

                _casc.Root.Dump();
            });
        }

        public void ScanFiles()
        {
            if (_casc == null || _root == null)
                return;

            if (scanForm == null)
            {
                scanForm = new ScanForm();
                scanForm.Initialize(_casc, _root);
            }

            scanForm.Reset();
            scanForm.ShowDialog();
        }

        public void UpdateListView(CASCFolder baseEntry, NoFlickerListView fileList, string filter)
        {
            Wildcard wildcard = new Wildcard(filter, false, RegexOptions.IgnoreCase);

            // Sort
            baseEntry.Entries = baseEntry.EntriesMirror.Where(v => v.Value is CASCFolder || (v.Value is CASCFile && wildcard.IsMatch(v.Value.Name))).
                OrderBy(v => v.Value, Sorter).ToDictionary(pair => pair.Key, pair => pair.Value);

            // Update
            fileList.Tag = baseEntry;
            fileList.VirtualListSize = 0;
            fileList.VirtualListSize = baseEntry.Entries.Count;

            if (fileList.VirtualListSize > 0)
            {
                fileList.EnsureVisible(0);
                fileList.SelectedIndex = 0;
                fileList.FocusedItem = fileList.Items[0];
            }
        }

        public void CreateTreeNodes(TreeNode node)
        {
            CASCFolder baseEntry = node.Tag as CASCFolder;

            // check if we have dummy node
            if (node.Nodes["tempnode"] != null)
            {
                // remove dummy node
                node.Nodes.Clear();

                var orderedEntries = baseEntry.Entries.OrderBy(v => v.Value.Name);

                // Create nodes dynamically
                foreach (var it in orderedEntries)
                {
                    CASCFolder entry = it.Value as CASCFolder;

                    if (entry != null && node.Nodes[entry.Name] == null)
                    {
                        TreeNode newNode = node.Nodes.Add(entry.Name);
                        newNode.Tag = entry;
                        newNode.Name = entry.Name;

                        if (entry.Entries.Count(v => v.Value is CASCFolder) > 0)
                            newNode.Nodes.Add(new TreeNode() { Name = "tempnode" }); // add dummy node
                    }
                }
            }
        }

        public void OpenStorage(string arg, bool online)
        {
            Cleanup();

            using (var initForm = new InitForm())
            {
                if (online)
                    initForm.LoadOnlineStorage(arg);
                else
                    initForm.LoadLocalStorage(arg);

                DialogResult res = initForm.ShowDialog();

                if (res != DialogResult.OK)
                    return;

                _casc = initForm.CASC;
                _root = initForm.Root;
            }

            Sorter.CASC = _casc;

            OnStorageChanged?.Invoke();
        }

        public void ChangeLocale(string locale)
        {
            if (_casc == null)
                return;

            OnCleanup?.Invoke();

            Settings.Default.LocaleFlags = (LocaleFlags)Enum.Parse(typeof(LocaleFlags), locale);

            _root = _casc.Root.SetFlags(Settings.Default.LocaleFlags, Settings.Default.ContentFlags);
            _casc.Root.MergeInstall(_casc.Install);

            OnStorageChanged?.Invoke();
        }

        public void ChangeContentFlags(bool set)
        {
            if (_casc == null)
                return;

            OnCleanup?.Invoke();

            if (set)
                Settings.Default.ContentFlags |= ContentFlags.LowViolence;
            else
                Settings.Default.ContentFlags &= ~ContentFlags.LowViolence;

            _root = _casc.Root.SetFlags(Settings.Default.LocaleFlags, Settings.Default.ContentFlags);
            _casc.Root.MergeInstall(_casc.Install);

            OnStorageChanged?.Invoke();
        }

        public void SetSort(int column)
        {
            Sorter.SortColumn = column;
            Sorter.Order = Sorter.Order == SortOrder.Ascending ? SortOrder.Descending : SortOrder.Ascending;
        }

        public void GetSize(NoFlickerListView fileList)
        {
            CASCFolder folder = fileList.Tag as CASCFolder;

            if (folder == null)
                return;

            if (!fileList.HasSelection)
                return;

            var files = folder.GetFiles(fileList.SelectedIndices.Cast<int>());

            long size = files.Sum(f => (long)f.GetSize(_casc));

            MessageBox.Show(string.Format(sizeNumberFmt, "{0:N} bytes", size));
        }

        public void PreviewFile(NoFlickerListView fileList)
        {
            CASCFolder folder = fileList.Tag as CASCFolder;

            if (folder == null)
                return;

            if (!fileList.HasSingleSelection)
                return;

            var file = folder.Entries.ElementAt(fileList.SelectedIndex).Value as CASCFile;

            var extension = Path.GetExtension(file.Name);

            if (extension != null)
            {
                switch (extension.ToLower())
                {
                    case ".blp":
                        {
                            PreviewBlp(file);
                            break;
                        }
                    case ".txt":
                    case ".ini":
                    case ".wtf":
                    case ".lua":
                    case ".toc":
                    case ".xml":
                    case ".htm":
                    case ".html":
                    case ".lst":
                        {
                            PreviewText(file);
                            break;
                        }
                    //case ".wav":
                    //case ".ogg":
                    //    {
                    //        PreviewSound(file);
                    //        break;
                    //    }
                    default:
                        {
                            MessageBox.Show(string.Format("Preview of {0} is not supported yet", extension), "Not supported file");
                            break;
                        }
                }
            }
        }

        private void PreviewText(CASCFile file)
        {
            using (var stream = _casc.OpenFile(file.Hash, file.FullName))
            {
                var text = new StreamReader(stream).ReadToEnd();
                var form = new Form { FormBorderStyle = FormBorderStyle.SizableToolWindow, StartPosition = FormStartPosition.CenterParent };
                form.Controls.Add(new TextBox
                {
                    Multiline = true,
                    ReadOnly = true,
                    Dock = DockStyle.Fill,
                    Text = text,
                    ScrollBars = ScrollBars.Both
                });
                form.Show();
            }
        }

        private void PreviewBlp(CASCFile file)
        {
            using (var stream = _casc.OpenFile(file.Hash, file.FullName))
            {
                var blp = new BlpFile(stream);
                var bitmap = blp.GetBitmap(0);
                var form = new ImagePreviewForm(bitmap);
                form.Show();
            }
        }

        public void CreateListViewItem(RetrieveVirtualItemEventArgs e, CASCFolder folder)
        {
            if (folder == null)
                return;

            if (e.ItemIndex < 0 || e.ItemIndex >= folder.Entries.Count)
                return;

            ICASCEntry entry = folder.Entries.ElementAt(e.ItemIndex).Value;

            var localeFlags = LocaleFlags.None;
            var contentFlags = ContentFlags.None;
            var size = "<DIR>";

            if (entry is CASCFile)
            {
                var rootInfosLocale = _casc.Root.GetEntries(entry.Hash);

                if (rootInfosLocale.Any())
                {
                    var enc = _casc.Encoding.GetEntry(rootInfosLocale.First().MD5);

                    if (enc != null)
                        size = enc.Size.ToString("N", sizeNumberFmt);
                    else
                        size = "0";

                    foreach (var rootInfo in rootInfosLocale)
                    {
                        if (rootInfo.Block != null)
                        {
                            localeFlags |= rootInfo.Block.LocaleFlags;
                            contentFlags |= rootInfo.Block.ContentFlags;
                        }
                    }
                }
                else
                {
                    var installInfos = _casc.Install.GetEntries(entry.Hash);

                    if (installInfos.Any())
                    {
                        var enc = _casc.Encoding.GetEntry(installInfos.First().MD5);

                        if (enc != null)
                            size = enc.Size.ToString("N", sizeNumberFmt);
                        else
                            size = "0";

                        //foreach (var rootInfo in rootInfosLocale)
                        //{
                        //    if (rootInfo.Block != null)
                        //    {
                        //        localeFlags |= rootInfo.Block.LocaleFlags;
                        //        contentFlags |= rootInfo.Block.ContentFlags;
                        //    }
                        //}
                    }
                }
            }

            e.Item = new ListViewItem(new string[]
            {
                entry.Name,
                entry is CASCFolder ? "Folder" : Path.GetExtension(entry.Name),
                localeFlags.ToString(),
                contentFlags.ToString(),
                size
            })
            { ImageIndex = entry is CASCFolder ? 0 : 2 };
        }

        public void Cleanup()
        {
            OnCleanup?.Invoke();

            Sorter.CASC = null;

            _root = null;

            if (_casc != null)
            {
                _casc.Clear();
                _casc = null;
            }
        }

        public void Search(NoFlickerListView fileList, SearchForVirtualItemEventArgs e)
        {
            bool ignoreCase = true;
            bool searchUp = false;
            int SelectedIndex = fileList.SelectedIndex;

            CASCFolder folder = fileList.Tag as CASCFolder;

            var comparisonType = ignoreCase
                                    ? StringComparison.InvariantCultureIgnoreCase
                                    : StringComparison.InvariantCulture;

            if (searchUp)
            {
                for (var i = SelectedIndex - 1; i >= 0; --i)
                {
                    var op = folder.Entries.ElementAt(i).Value.Name;
                    if (op.IndexOf(e.Text, comparisonType) != -1)
                    {
                        e.Index = i;
                        break;
                    }
                }
            }
            else
            {
                for (int i = SelectedIndex + 1; i < fileList.Items.Count; ++i)
                {
                    var op = folder.Entries.ElementAt(i).Value.Name;
                    if (op.IndexOf(e.Text, comparisonType) != -1)
                    {
                        e.Index = i;
                        break;
                    }
                }
            }
        }

        public void ExportListFile()
        {
            using (StreamWriter sw = new StreamWriter("listfile_export.txt"))
            {
                foreach (var file in Root.GetFiles(null, true).OrderBy(f => f.FullName, StringComparer.OrdinalIgnoreCase))
                    sw.WriteLine(file.FullName);
            }
        }

        public void ExtractCASCSystemFiles()
        {
            if (_casc == null)
                return;

            var files = new Dictionary<string, byte[]>()
            {
                { "root", _casc.Encoding.GetEntry(_casc.Config.RootMD5).Key },
                { "install", _casc.Encoding.GetEntry(_casc.Config.InstallMD5).Key },
                { "encoding", _casc.Config.EncodingKey },
                { "download", _casc.Encoding.GetEntry(_casc.Config.DownloadMD5).Key }
            };

            foreach (var file in files)
            {
                _casc.ExtractFile(file.Value, ".", file.Key);
            }
        }
    }
}
