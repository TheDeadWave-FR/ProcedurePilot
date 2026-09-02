using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Xml.Linq;

namespace ProcedurePilot
{
    internal sealed class ProcedureListDefinition
    {
        internal string Id;
        internal string Name;
        internal DateTime CreatedAtUtc;
        internal DateTime ModifiedAtUtc;
        internal readonly List<string> Procedures = new List<string>();
    }

    internal static class ProcedureListStorage
    {
        internal const string FileName = "Lists.xml";

        internal static List<ProcedureListDefinition> Load(string path)
        {
            var lists = new List<ProcedureListDefinition>();
            if (!File.Exists(path)) return lists;

            XDocument document = XDocument.Load(path);
            XElement root = document.Root;
            if (root == null || root.Name.LocalName != "ProcedureLists")
                throw new InvalidDataException(UiText.Get(
                    "Le fichier Lists.xml n’est pas un fichier de listes de procédures valide.",
                    "The Lists.xml file is not a valid procedure-list file."));

            foreach (XElement element in root.Elements("List"))
            {
                string name = ReadAttribute(element, "name");
                if (string.IsNullOrWhiteSpace(name)) continue;
                DateTime created;
                DateTime modified;
                var list = new ProcedureListDefinition
                {
                    Id = ReadAttribute(element, "id") ?? Guid.NewGuid().ToString("N"),
                    Name = name.Trim(),
                    CreatedAtUtc = DateTime.TryParse(ReadAttribute(element, "createdAtUtc"), out created) ? created.ToUniversalTime() : DateTime.UtcNow,
                    ModifiedAtUtc = DateTime.TryParse(ReadAttribute(element, "modifiedAtUtc"), out modified) ? modified.ToUniversalTime() : DateTime.UtcNow
                };
                foreach (XElement procedure in element.Elements("Procedure"))
                {
                    string procedureName = ReadAttribute(procedure, "name");
                    if (!string.IsNullOrWhiteSpace(procedureName)
                        && !list.Procedures.Contains(procedureName.Trim(), StringComparer.OrdinalIgnoreCase))
                        list.Procedures.Add(procedureName.Trim());
                }
                if (list.Procedures.Count > 0) lists.Add(list);
            }
            return lists.OrderBy(value => value.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        internal static void Save(string path, IEnumerable<ProcedureListDefinition> source)
        {
            string fullPath = Path.GetFullPath(path);
            string folder = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrWhiteSpace(folder))
                throw new InvalidOperationException(UiText.Get(
                    "Le dossier de Lists.xml est invalide.",
                    "The Lists.xml folder is invalid."));
            Directory.CreateDirectory(folder);

            ProcedureListDefinition[] lists = source
                .Where(value => value != null && !string.IsNullOrWhiteSpace(value.Name) && value.Procedures.Count > 0)
                .OrderBy(value => value.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            string duplicateName = lists.GroupBy(value => value.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1).Select(group => group.Key).FirstOrDefault();
            if (duplicateName != null)
                throw new InvalidOperationException(UiText.Format(
                    "Deux listes portent le nom « {0} ».",
                    "Two lists are named “{0}”.",
                    duplicateName));

            var root = new XElement("ProcedureLists",
                new XAttribute("version", "1"),
                new XAttribute("updatedAtUtc", DateTime.UtcNow.ToString("o")),
                new XAttribute("count", lists.Length));
            foreach (ProcedureListDefinition list in lists)
            {
                var element = new XElement("List",
                    new XAttribute("id", string.IsNullOrWhiteSpace(list.Id) ? Guid.NewGuid().ToString("N") : list.Id),
                    new XAttribute("name", list.Name.Trim()),
                    new XAttribute("createdAtUtc", list.CreatedAtUtc.ToUniversalTime().ToString("o")),
                    new XAttribute("modifiedAtUtc", list.ModifiedAtUtc.ToUniversalTime().ToString("o")),
                    new XAttribute("count", list.Procedures.Count));
                for (int index = 0; index < list.Procedures.Count; index++)
                {
                    string procedureName = list.Procedures[index];
                    var procedure = new XElement("Procedure",
                        new XAttribute("order", index + 1),
                        new XAttribute("name", procedureName));
                    string code = ProcedureTag.GetCode(procedureName);
                    if (code.Length > 0) procedure.Add(new XAttribute("code", code));
                    element.Add(procedure);
                }
                root.Add(element);
            }

            var document = new XDocument(
                new XDeclaration("1.0", "utf-8", "yes"),
                new XComment("Procedure lists created by Procedure Pilot."),
                root);
            string tempPath = Path.Combine(folder, ".procedurepilot-lists-" + Guid.NewGuid().ToString("N") + ".tmp");
            document.Save(tempPath);
            try
            {
                if (File.Exists(fullPath))
                {
                    try { File.Replace(tempPath, fullPath, null, true); }
                    catch
                    {
                        File.Copy(tempPath, fullPath, true);
                        File.Delete(tempPath);
                    }
                }
                else File.Move(tempPath, fullPath);
            }
            finally
            {
                if (File.Exists(tempPath)) try { File.Delete(tempPath); } catch { }
            }
        }

        internal static bool SynchronizeWithLibrary(List<ProcedureListDefinition> lists, IEnumerable<string> currentNames)
        {
            string[] names = currentNames.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            ProcedureTag.EnsureUniqueCodes(names);
            bool changed = false;
            foreach (ProcedureListDefinition list in lists)
            {
                bool listChanged = false;
                var resolved = new List<string>();
                foreach (string storedName in list.Procedures)
                {
                    string current = names.FirstOrDefault(value => string.Equals(value, storedName, StringComparison.OrdinalIgnoreCase));
                    if (current == null)
                    {
                        string code = ProcedureTag.GetCode(storedName);
                        if (code.Length > 0)
                            current = names.FirstOrDefault(value => string.Equals(ProcedureTag.GetCode(value), code, StringComparison.OrdinalIgnoreCase));
                    }
                    if (current != null && !resolved.Contains(current, StringComparer.OrdinalIgnoreCase)) resolved.Add(current);
                    if (!string.Equals(current, storedName, StringComparison.Ordinal)) listChanged = true;
                }
                if (resolved.Count != list.Procedures.Count) listChanged = true;
                if (listChanged)
                {
                    list.Procedures.Clear();
                    list.Procedures.AddRange(resolved);
                    list.ModifiedAtUtc = DateTime.UtcNow;
                    changed = true;
                }
            }
            int removed = lists.RemoveAll(value => value.Procedures.Count == 0);
            return changed || removed > 0;
        }

        internal static int UpdateProcedureNames(string path, IDictionary<string, string> renames)
        {
            if (renames == null || renames.Count == 0 || !File.Exists(path)) return 0;
            List<ProcedureListDefinition> lists = Load(path);
            int updated = 0;
            foreach (ProcedureListDefinition list in lists)
            {
                bool listChanged = false;
                for (int index = 0; index < list.Procedures.Count; index++)
                {
                    string newName;
                    if (!renames.TryGetValue(list.Procedures[index], out newName)
                        || string.IsNullOrWhiteSpace(newName)
                        || string.Equals(list.Procedures[index], newName, StringComparison.Ordinal)) continue;
                    list.Procedures[index] = newName;
                    updated++;
                    listChanged = true;
                }
                if (listChanged) list.ModifiedAtUtc = DateTime.UtcNow;
            }
            if (updated > 0) Save(path, lists);
            return updated;
        }

        internal static void CopyToNewDataFolder(string previousFolder, string newFolder)
        {
            if (string.IsNullOrWhiteSpace(previousFolder) || string.IsNullOrWhiteSpace(newFolder)) return;
            string source = Path.Combine(Path.GetFullPath(previousFolder), FileName);
            string destination = Path.Combine(Path.GetFullPath(newFolder), FileName);
            if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase) || !File.Exists(source) || File.Exists(destination)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            File.Copy(source, destination, false);
        }

        private static string ReadAttribute(XElement element, string name)
        {
            XAttribute attribute = element.Attribute(name);
            return attribute == null ? null : attribute.Value;
        }

    }

    internal sealed class ProcedureListsForm : Form
    {
        private const int WorkspaceWriteTimeoutMs = 10000;
        private const int WorkspaceReadTimeoutMs = 250;
        private readonly string storagePath;
        private readonly string dataFolder;
        private readonly string[] procedureNames;
        private readonly Action<string> openProcedure;
        private readonly Action<string> writeLog;
        private readonly List<ProcedureListDefinition> lists;
        private readonly ListBox listBox;
        private readonly ListView procedureList;
        private readonly Label detailsTitle;
        private readonly Button editButton;
        private readonly Button deleteButton;
        private readonly Button openButton;
        private readonly System.Windows.Forms.Timer storagePollTimer;
        private object storageStamp;
        private bool refreshingLists;
        private bool storageOperationInProgress;

        internal ProcedureListsForm(string path, IEnumerable<string> availableProcedures, Action<string> openAction, Action<string> logAction)
        {
            storagePath = path;
            dataFolder = Path.GetDirectoryName(Path.GetFullPath(storagePath));
            if (string.IsNullOrWhiteSpace(dataFolder))
                throw new InvalidOperationException(UiText.Get(
                    "Le dossier de Lists.xml est invalide.",
                    "The Lists.xml folder is invalid."));
            procedureNames = availableProcedures.OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase).ToArray();
            openProcedure = openAction;
            writeLog = logAction;
            lists = new List<ProcedureListDefinition>();
            IDisposable initialLease = WorkspaceMultiUser.TryAcquire(dataFolder, WorkspaceWriteTimeoutMs);
            if (initialLease == null)
            {
                if (writeLog != null)
                    writeLog(UiText.Get(
                        "La synchronisation initiale des listes est reportée : un autre utilisateur modifie l’espace de travail.",
                        "Initial list synchronization was deferred because another user is modifying the workspace."));
                lists.AddRange(ProcedureListStorage.Load(storagePath));
            }
            else
            {
                using (initialLease)
                {
                    lists.AddRange(ProcedureListStorage.Load(storagePath));
                    if (ProcedureListStorage.SynchronizeWithLibrary(lists, procedureNames))
                    {
                        ProcedureListStorage.Save(storagePath, lists);
                        WorkspaceMultiUser.PublishChange(dataFolder, "lists");
                        if (writeLog != null)
                            writeLog(UiText.Get(
                                "Listes de procédures synchronisées avec la bibliothèque.",
                                "Procedure lists synchronized with the library."));
                    }
                }
            }

            Text = UiText.Get("Listes de procédures", "Procedure lists");
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(820, 500);
            Size = new Size(940, 600);
            BackColor = Color.FromArgb(248, 250, 252);
            Font = new Font("Segoe UI", 9F);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(18), BackColor = BackColor };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 37F));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 63F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54F));
            Controls.Add(layout);

            var left = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(14), Margin = new Padding(0, 0, 8, 0) };
            layout.Controls.Add(left, 0, 0);
            var leftLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0), Padding = new Padding(0), BackColor = Color.White };
            leftLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            leftLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32F));
            leftLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            left.Controls.Add(leftLayout);
            var leftTitle = new Label { Text = UiText.Get("MES LISTES", "MY LISTS"), Dock = DockStyle.Fill, ForeColor = Color.FromArgb(30, 41, 59), Font = new Font("Segoe UI Semibold", 10F, FontStyle.Bold) };
            leftLayout.Controls.Add(leftTitle, 0, 0);
            listBox = new ListBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, Font = new Font("Segoe UI", 10F), IntegralHeight = false, HorizontalScrollbar = true };
            listBox.SelectedIndexChanged += delegate { if (!refreshingLists) RefreshDetails(); };
            listBox.DoubleClick += delegate { EditSelected(); };
            leftLayout.Controls.Add(listBox, 0, 1);

            var right = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(14), Margin = new Padding(8, 0, 0, 0) };
            layout.Controls.Add(right, 1, 0);
            var rightLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0), Padding = new Padding(0), BackColor = Color.White };
            rightLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            rightLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));
            rightLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            right.Controls.Add(rightLayout);
            detailsTitle = new Label { Text = UiText.Get("Sélectionnez une liste", "Select a list"), Dock = DockStyle.Fill, ForeColor = Color.FromArgb(30, 41, 59), Font = new Font("Segoe UI Semibold", 11F, FontStyle.Bold), AutoEllipsis = true };
            rightLayout.Controls.Add(detailsTitle, 0, 0);
            procedureList = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, BorderStyle = BorderStyle.None, Font = new Font("Segoe UI", 9.5F) };
            procedureList.Columns.Add("#", 42, HorizontalAlignment.Right);
            procedureList.Columns.Add(UiText.Get("Procédure", "Procedure"), 430);
            procedureList.DoubleClick += delegate { OpenSelectedProcedure(); };
            procedureList.ClientSizeChanged += delegate { ResizeProcedureColumn(); };
            rightLayout.Controls.Add(procedureList, 0, 1);

            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = new Padding(0, 8, 0, 0), Margin = new Padding(0) };
            layout.Controls.Add(actions, 0, 1);
            layout.SetColumnSpan(actions, 2);
            Button newButton = NewButton(UiText.Get("Nouvelle liste", "New list"), Color.FromArgb(37, 99, 235), Color.White, 130);
            newButton.Enabled = procedureNames.Length > 0;
            newButton.Click += delegate { CreateList(); };
            actions.Controls.Add(newButton);
            editButton = NewButton(UiText.Get("Modifier", "Edit"), Color.FromArgb(51, 65, 85), Color.White, 105);
            editButton.Click += delegate { EditSelected(); };
            actions.Controls.Add(editButton);
            deleteButton = NewButton(UiText.Get("Supprimer", "Delete"), Color.White, Color.FromArgb(185, 28, 28), 105);
            deleteButton.Click += delegate { DeleteSelected(); };
            actions.Controls.Add(deleteButton);
            openButton = NewButton(UiText.Get("Ouvrir la procédure", "Open procedure"), Color.White, Color.FromArgb(30, 41, 59), 150);
            openButton.Click += delegate { OpenSelectedProcedure(); };
            actions.Controls.Add(openButton);
            Button closeButton = NewButton(UiText.Get("Fermer", "Close"), Color.White, Color.FromArgb(30, 41, 59), 90);
            closeButton.Click += delegate { Close(); };
            actions.Controls.Add(closeButton);
            CancelButton = closeButton;

            RefreshLists(null);
            UpdateStorageStamp();
            storagePollTimer = new System.Windows.Forms.Timer { Interval = 1500 };
            storagePollTimer.Tick += StoragePollTimerTick;
            storagePollTimer.Start();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && storagePollTimer != null)
            {
                storagePollTimer.Stop();
                storagePollTimer.Tick -= StoragePollTimerTick;
                storagePollTimer.Dispose();
            }
            base.Dispose(disposing);
        }

        private static Button NewButton(string text, Color background, Color foreground, int width)
        {
            var button = new Button { Text = text, Width = width, Height = 36, BackColor = background, ForeColor = foreground, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 0, 8, 0), Cursor = Cursors.Hand, Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold), UseVisualStyleBackColor = false };
            button.FlatAppearance.BorderColor = background == Color.White ? Color.FromArgb(203, 213, 225) : background;
            return button;
        }

        private void RefreshLists(string selectedId)
        {
            refreshingLists = true;
            listBox.BeginUpdate();
            listBox.Items.Clear();
            foreach (ProcedureListDefinition list in lists.OrderBy(value => value.Name, StringComparer.CurrentCultureIgnoreCase))
                listBox.Items.Add(new ListDisplayItem(list));
            listBox.EndUpdate();
            if (!string.IsNullOrWhiteSpace(selectedId))
            {
                for (int index = 0; index < listBox.Items.Count; index++)
                    if (string.Equals(((ListDisplayItem)listBox.Items[index]).Value.Id, selectedId, StringComparison.OrdinalIgnoreCase)) { listBox.SelectedIndex = index; break; }
            }
            if (listBox.SelectedIndex < 0 && listBox.Items.Count > 0) listBox.SelectedIndex = 0;
            refreshingLists = false;
            RefreshDetails();
        }

        private ProcedureListDefinition SelectedList
        {
            get { return listBox.SelectedItem == null ? null : ((ListDisplayItem)listBox.SelectedItem).Value; }
        }

        private void RefreshDetails()
        {
            ProcedureListDefinition selected = SelectedList;
            procedureList.Items.Clear();
            detailsTitle.Text = selected == null
                ? UiText.Get("Sélectionnez une liste", "Select a list")
                : UiText.Format(
                    selected.Procedures.Count == 1 ? "{0}  ·  {1} procédure" : "{0}  ·  {1} procédures",
                    selected.Procedures.Count == 1 ? "{0}  ·  {1} procedure" : "{0}  ·  {1} procedures",
                    selected.Name,
                    selected.Procedures.Count);
            if (selected != null)
            {
                for (int index = 0; index < selected.Procedures.Count; index++)
                {
                    var row = new ListViewItem(new[] { (index + 1).ToString(), selected.Procedures[index] });
                    row.Tag = selected.Procedures[index];
                    procedureList.Items.Add(row);
                }
            }
            editButton.Enabled = selected != null;
            deleteButton.Enabled = selected != null;
            openButton.Enabled = selected != null && procedureList.SelectedItems.Count > 0;
            procedureList.SelectedIndexChanged -= ProcedureSelectionChanged;
            procedureList.SelectedIndexChanged += ProcedureSelectionChanged;
            ResizeProcedureColumn();
        }

        private void ResizeProcedureColumn()
        {
            if (procedureList == null || procedureList.Columns.Count < 2) return;
            procedureList.Columns[1].Width = Math.Max(180, procedureList.ClientSize.Width - procedureList.Columns[0].Width - 6);
        }

        private void ProcedureSelectionChanged(object sender, EventArgs e)
        {
            openButton.Enabled = procedureList.SelectedItems.Count > 0;
        }

        private void CreateList()
        {
            using (var dialog = new ProcedureListEditorForm(null, procedureNames, lists.Select(value => value.Name)))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                IDisposable lease = AcquireWorkspaceLease();
                if (lease == null) return;
                storageOperationInProgress = true;
                try
                {
                    bool duplicateName = false;
                    using (lease)
                    {
                        List<ProcedureListDefinition> currentLists = ProcedureListStorage.Load(storagePath);
                        if (currentLists.Any(value => string.Equals(value.Name, dialog.Result.Name, StringComparison.OrdinalIgnoreCase)))
                        {
                            ReplaceLists(currentLists, null);
                            duplicateName = true;
                        }
                        else
                        {
                            currentLists.Add(dialog.Result);
                            ProcedureListStorage.Save(storagePath, currentLists);
                            WorkspaceMultiUser.PublishChange(dataFolder, "lists");
                            ReplaceLists(currentLists, dialog.Result.Id);
                        }
                        UpdateStorageStamp();
                    }
                    if (duplicateName)
                    {
                        ShowDuplicateNameConflict();
                        return;
                    }
                    if (writeLog != null)
                        writeLog(UiText.Format(
                            "Liste créée : {0}.",
                            "List created: {0}.",
                            dialog.Result.Name));
                }
                finally
                {
                    storageOperationInProgress = false;
                }
            }
        }

        private void EditSelected()
        {
            ProcedureListDefinition selected = SelectedList;
            if (selected == null) return;
            string selectedId = selected.Id;
            DateTime originallyLoadedModifiedAtUtc = selected.ModifiedAtUtc;
            using (var dialog = new ProcedureListEditorForm(selected, procedureNames, lists.Where(value => value != selected).Select(value => value.Name)))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                IDisposable lease = AcquireWorkspaceLease();
                if (lease == null) return;
                storageOperationInProgress = true;
                try
                {
                    bool editConflict = false;
                    bool duplicateName = false;
                    using (lease)
                    {
                        List<ProcedureListDefinition> currentLists = ProcedureListStorage.Load(storagePath);
                        ProcedureListDefinition current = currentLists.FirstOrDefault(value => string.Equals(value.Id, selectedId, StringComparison.OrdinalIgnoreCase));
                        if (current == null || !SameVersion(current.ModifiedAtUtc, originallyLoadedModifiedAtUtc))
                        {
                            ReplaceLists(currentLists, selectedId);
                            editConflict = true;
                        }
                        else if (currentLists.Any(value => value != current && string.Equals(value.Name, dialog.Result.Name, StringComparison.OrdinalIgnoreCase)))
                        {
                            ReplaceLists(currentLists, selectedId);
                            duplicateName = true;
                        }
                        else
                        {
                            current.Name = dialog.Result.Name;
                            current.ModifiedAtUtc = DateTime.UtcNow;
                            current.Procedures.Clear();
                            current.Procedures.AddRange(dialog.Result.Procedures);
                            ProcedureListStorage.Save(storagePath, currentLists);
                            WorkspaceMultiUser.PublishChange(dataFolder, "lists");
                            ReplaceLists(currentLists, current.Id);
                        }
                        UpdateStorageStamp();
                    }
                    if (editConflict)
                    {
                        ShowEditConflict();
                        return;
                    }
                    if (duplicateName)
                    {
                        ShowDuplicateNameConflict();
                        return;
                    }
                    if (writeLog != null)
                        writeLog(UiText.Format(
                            "Liste modifiée : {0}.",
                            "List updated: {0}.",
                            dialog.Result.Name));
                }
                finally
                {
                    storageOperationInProgress = false;
                }
            }
        }

        private void DeleteSelected()
        {
            ProcedureListDefinition selected = SelectedList;
            if (selected == null) return;
            string selectedId = selected.Id;
            string selectedName = selected.Name;
            DateTime originallyLoadedModifiedAtUtc = selected.ModifiedAtUtc;
            if (MessageBox.Show(
                UiText.Format(
                    "Supprimer la liste « {0} » ?\r\n\r\nLes procédures elles-mêmes ne seront pas supprimées.",
                    "Delete the “{0}” list?\r\n\r\nThe procedures themselves will not be deleted.",
                    selected.Name),
                UiText.Get("Supprimer la liste", "Delete list"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning) != DialogResult.Yes) return;
            IDisposable lease = AcquireWorkspaceLease();
            if (lease == null) return;
            storageOperationInProgress = true;
            try
            {
                bool editConflict = false;
                using (lease)
                {
                    List<ProcedureListDefinition> currentLists = ProcedureListStorage.Load(storagePath);
                    ProcedureListDefinition current = currentLists.FirstOrDefault(value => string.Equals(value.Id, selectedId, StringComparison.OrdinalIgnoreCase));
                    if (current == null || !SameVersion(current.ModifiedAtUtc, originallyLoadedModifiedAtUtc))
                    {
                        ReplaceLists(currentLists, selectedId);
                        editConflict = true;
                    }
                    else
                    {
                        currentLists.Remove(current);
                        ProcedureListStorage.Save(storagePath, currentLists);
                        WorkspaceMultiUser.PublishChange(dataFolder, "lists");
                        ReplaceLists(currentLists, null);
                    }
                    UpdateStorageStamp();
                }
                if (editConflict)
                {
                    ShowEditConflict();
                    return;
                }
                if (writeLog != null)
                    writeLog(UiText.Format(
                        "Liste supprimée : {0}.",
                        "List deleted: {0}.",
                        selectedName));
            }
            finally
            {
                storageOperationInProgress = false;
            }
        }

        private void StoragePollTimerTick(object sender, EventArgs e)
        {
            if (storageOperationInProgress || IsDisposed || Disposing) return;

            object currentStamp;
            try
            {
                currentStamp = WorkspaceMultiUser.GetFileStamp(storagePath);
            }
            catch
            {
                return;
            }
            if (object.Equals(storageStamp, currentStamp)) return;

            IDisposable lease = null;
            try
            {
                lease = WorkspaceMultiUser.TryAcquire(dataFolder, WorkspaceReadTimeoutMs);
                if (lease == null) return;
                storageOperationInProgress = true;
                using (lease)
                {
                    string selectedId = SelectedList == null ? null : SelectedList.Id;
                    List<ProcedureListDefinition> currentLists = ProcedureListStorage.Load(storagePath);
                    ReplaceLists(currentLists, selectedId);
                    UpdateStorageStamp();
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (InvalidDataException exception)
            {
                if (writeLog != null)
                    writeLog(UiText.Format(
                        "Actualisation des listes reportée : {0}",
                        "List refresh deferred: {0}",
                        exception.Message));
            }
            catch (Exception exception)
            {
                if (writeLog != null)
                    writeLog(UiText.Format(
                        "Actualisation des listes reportée : {0}",
                        "List refresh deferred: {0}",
                        exception.Message));
            }
            finally
            {
                storageOperationInProgress = false;
            }
        }

        private IDisposable AcquireWorkspaceLease()
        {
            IDisposable lease;
            try
            {
                lease = WorkspaceMultiUser.TryAcquire(dataFolder, WorkspaceWriteTimeoutMs);
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    UiText.Format(
                        "Impossible d’accéder à l’espace de travail partagé.\r\n\r\n{0}",
                        "The shared workspace could not be accessed.\r\n\r\n{0}",
                        exception.Message),
                    UiText.Get("Espace de travail indisponible", "Workspace unavailable"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return null;
            }
            if (lease != null) return lease;

            MessageBox.Show(
                UiText.Get(
                    "Un autre utilisateur effectue une modification. Réessayez dans quelques instants.",
                    "Another user is making a change. Try again in a few moments."),
                UiText.Get("Espace de travail occupé", "Workspace busy"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return null;
        }

        private void ReplaceLists(IEnumerable<ProcedureListDefinition> currentLists, string selectedId)
        {
            lists.Clear();
            lists.AddRange(currentLists);
            RefreshLists(selectedId);
        }

        private void UpdateStorageStamp()
        {
            try { storageStamp = WorkspaceMultiUser.GetFileStamp(storagePath); }
            catch { storageStamp = null; }
        }

        private static bool SameVersion(DateTime left, DateTime right)
        {
            return left.ToUniversalTime().Ticks == right.ToUniversalTime().Ticks;
        }

        private void ShowEditConflict()
        {
            MessageBox.Show(
                UiText.Get(
                    "Cette liste a été modifiée ou supprimée par un autre utilisateur. Vos changements n’ont pas été enregistrés et la version la plus récente a été rechargée.",
                    "Another user modified or deleted this list. Your changes were not saved, and the latest version has been reloaded."),
                UiText.Get("Conflit de modification", "Edit conflict"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        private void ShowDuplicateNameConflict()
        {
            MessageBox.Show(
                UiText.Get(
                    "Une autre liste porte déjà ce nom. La version la plus récente des listes a été rechargée.",
                    "Another list already uses this name. The latest version of the lists has been reloaded."),
                UiText.Get("Nom déjà utilisé", "Name already in use"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        private void OpenSelectedProcedure()
        {
            if (procedureList.SelectedItems.Count == 0 || openProcedure == null) return;
            openProcedure(procedureList.SelectedItems[0].Tag as string);
        }

        private sealed class ListDisplayItem
        {
            internal readonly ProcedureListDefinition Value;
            internal ListDisplayItem(ProcedureListDefinition value) { Value = value; }
            public override string ToString() { return Value.Name + "  (" + Value.Procedures.Count + ")"; }
        }
    }

    internal sealed class ProcedureListEditorForm : Form
    {
        private readonly TextBox nameBox;
        private readonly ListBox availableList;
        private readonly ListBox selectedList;
        private readonly HashSet<string> reservedNames;
        internal ProcedureListDefinition Result { get; private set; }

        internal ProcedureListEditorForm(ProcedureListDefinition existing, IEnumerable<string> availableProcedures, IEnumerable<string> otherListNames)
        {
            reservedNames = new HashSet<string>(otherListNames, StringComparer.OrdinalIgnoreCase);
            Text = existing == null
                ? UiText.Get("Nouvelle liste de procédures", "New procedure list")
                : UiText.Get("Modifier la liste", "Edit list");
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(780, 520);
            Size = new Size(860, 580);
            BackColor = Color.FromArgb(248, 250, 252);
            Font = new Font("Segoe UI", 9F);

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(18), BackColor = BackColor };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54F));
            Controls.Add(root);
            root.Controls.Add(new Label { Text = UiText.Get("NOM DE LA LISTE", "LIST NAME"), Dock = DockStyle.Fill, ForeColor = Color.FromArgb(71, 85, 105), Font = new Font("Segoe UI Semibold", 8.5F, FontStyle.Bold) }, 0, 0);
            nameBox = new TextBox { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 10.5F), Text = existing == null ? "" : existing.Name };
            root.Controls.Add(nameBox, 0, 1);

            var chooser = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, Margin = new Padding(0, 12, 0, 8) };
            chooser.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46F));
            chooser.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72F));
            chooser.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 54F));
            chooser.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
            chooser.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.Controls.Add(chooser, 0, 2);
            chooser.Controls.Add(new Label { Text = UiText.Get("PROCÉDURES DISPONIBLES", "AVAILABLE PROCEDURES"), Dock = DockStyle.Fill, ForeColor = Color.FromArgb(71, 85, 105), Font = new Font("Segoe UI Semibold", 8.5F, FontStyle.Bold) }, 0, 0);
            chooser.Controls.Add(new Label { Text = UiText.Get("ORDRE DE LA LISTE", "LIST ORDER"), Dock = DockStyle.Fill, ForeColor = Color.FromArgb(71, 85, 105), Font = new Font("Segoe UI Semibold", 8.5F, FontStyle.Bold) }, 2, 0);
            availableList = new ListBox { Dock = DockStyle.Fill, SelectionMode = SelectionMode.MultiExtended, IntegralHeight = false, Font = new Font("Segoe UI", 9.5F) };
            selectedList = new ListBox { Dock = DockStyle.Fill, SelectionMode = SelectionMode.MultiExtended, IntegralHeight = false, Font = new Font("Segoe UI", 9.5F) };
            chooser.Controls.Add(availableList, 0, 1);
            chooser.Controls.Add(selectedList, 2, 1);

            var centerButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(10, 42, 10, 0) };
            chooser.Controls.Add(centerButtons, 1, 1);
            Button addButton = SmallButton(">");
            addButton.Click += delegate { AddProcedures(); };
            centerButtons.Controls.Add(addButton);
            Button removeButton = SmallButton("<");
            removeButton.Click += delegate { RemoveProcedures(); };
            centerButtons.Controls.Add(removeButton);
            Button upButton = SmallButton("▲");
            upButton.Margin = new Padding(0, 24, 0, 6);
            upButton.Click += delegate { MoveSelected(-1); };
            centerButtons.Controls.Add(upButton);
            Button downButton = SmallButton("▼");
            downButton.Click += delegate { MoveSelected(1); };
            centerButtons.Controls.Add(downButton);
            availableList.DoubleClick += delegate { AddProcedures(); };
            selectedList.DoubleClick += delegate { RemoveProcedures(); };

            string[] selected = existing == null ? new string[0] : existing.Procedures.ToArray();
            foreach (string value in selected) selectedList.Items.Add(value);
            foreach (string value in availableProcedures.OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase))
                if (!selected.Contains(value, StringComparer.OrdinalIgnoreCase)) availableList.Items.Add(value);

            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0, 9, 0, 0) };
            root.Controls.Add(actions, 0, 3);
            Button saveButton = DialogButton(UiText.Get("Enregistrer", "Save"), Color.FromArgb(37, 99, 235), Color.White, 120);
            saveButton.Click += SaveClicked;
            actions.Controls.Add(saveButton);
            Button cancelButton = DialogButton(UiText.Get("Annuler", "Cancel"), Color.White, Color.FromArgb(30, 41, 59), 100);
            cancelButton.DialogResult = DialogResult.Cancel;
            actions.Controls.Add(cancelButton);
            AcceptButton = saveButton;
            CancelButton = cancelButton;

            if (existing != null)
            {
                Result = new ProcedureListDefinition { Id = existing.Id, CreatedAtUtc = existing.CreatedAtUtc };
            }
            else Result = new ProcedureListDefinition { Id = Guid.NewGuid().ToString("N"), CreatedAtUtc = DateTime.UtcNow };
        }

        private static Button SmallButton(string text)
        {
            return new Button { Text = text, Width = 44, Height = 32, Margin = new Padding(0, 0, 0, 6), FlatStyle = FlatStyle.Flat, BackColor = Color.White, ForeColor = Color.FromArgb(30, 41, 59), Cursor = Cursors.Hand, UseVisualStyleBackColor = false };
        }

        private static Button DialogButton(string text, Color background, Color foreground, int width)
        {
            var button = new Button { Text = text, Width = width, Height = 36, BackColor = background, ForeColor = foreground, FlatStyle = FlatStyle.Flat, Margin = new Padding(8, 0, 0, 0), Cursor = Cursors.Hand, Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold), UseVisualStyleBackColor = false };
            button.FlatAppearance.BorderColor = background == Color.White ? Color.FromArgb(203, 213, 225) : background;
            return button;
        }

        private void AddProcedures()
        {
            string[] values = availableList.SelectedItems.Cast<string>().ToArray();
            foreach (string value in values)
            {
                availableList.Items.Remove(value);
                selectedList.Items.Add(value);
            }
        }

        private void RemoveProcedures()
        {
            string[] values = selectedList.SelectedItems.Cast<string>().ToArray();
            foreach (string value in values)
            {
                selectedList.Items.Remove(value);
                availableList.Items.Add(value);
            }
            SortListBox(availableList);
        }

        private static void SortListBox(ListBox box)
        {
            string[] values = box.Items.Cast<string>().OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase).ToArray();
            box.Items.Clear();
            box.Items.AddRange(values);
        }

        private void MoveSelected(int direction)
        {
            if (selectedList.SelectedIndices.Count != 1) return;
            int index = selectedList.SelectedIndex;
            int destination = index + direction;
            if (destination < 0 || destination >= selectedList.Items.Count) return;
            object value = selectedList.Items[index];
            selectedList.Items.RemoveAt(index);
            selectedList.Items.Insert(destination, value);
            selectedList.SelectedIndex = destination;
        }

        private void SaveClicked(object sender, EventArgs e)
        {
            string title = nameBox.Text.Trim();
            if (title.Length == 0)
            {
                MessageBox.Show(
                    UiText.Get("Saisissez un nom pour la liste.", "Enter a name for the list."),
                    UiText.Get("Nom obligatoire", "Name required"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                nameBox.Focus();
                return;
            }
            if (reservedNames.Contains(title))
            {
                MessageBox.Show(
                    UiText.Get("Une liste porte déjà ce nom.", "A list already uses this name."),
                    UiText.Get("Nom déjà utilisé", "Name already in use"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                nameBox.Focus();
                return;
            }
            if (selectedList.Items.Count == 0)
            {
                MessageBox.Show(
                    UiText.Get("Ajoutez au moins une procédure existante à la liste.", "Add at least one existing procedure to the list."),
                    UiText.Get("Liste vide", "Empty list"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }
            Result.Name = title;
            Result.ModifiedAtUtc = DateTime.UtcNow;
            Result.Procedures.Clear();
            Result.Procedures.AddRange(selectedList.Items.Cast<string>());
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
