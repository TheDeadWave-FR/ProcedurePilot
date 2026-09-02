using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Linq;

namespace ProcedurePilot
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            string reportPath = GetArg(args, "--report");
            try
            {
                Run(args);
            }
            catch (Exception ex)
            {
                string message = ex.Message;
                if (!string.IsNullOrWhiteSpace(reportPath))
                {
                    try { File.WriteAllText(reportPath, "ERROR\r\n" + message, Encoding.UTF8); }
                    catch { }
                    return;
                }

                MessageBox.Show(
                    message,
                    UiText.Get("Démarrage impossible", "Unable to start"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private static void Run(string[] args)
        {
            string root = ResolveRoot(args);
            string reportPath = GetArg(args, "--report");
            string localInput = GetArg(args, "--convert-local");
            if (!string.IsNullOrWhiteSpace(localInput))
            {
                UiText.SetLanguage(WorkspaceSettings.Load(SettingsStorage.ResolveSettingsPath(root), root).Language);
                string localOutput = GetArg(args, "--output");
                if (string.IsNullOrWhiteSpace(localOutput)) throw new ArgumentException(UiText.Get(
                    "Le paramètre --output est obligatoire avec --convert-local.",
                    "The --output parameter is required with --convert-local."));
                LocalDocxPdfConverter.Convert(localInput, localOutput, DocumentSupport.FindLocalPdfRenderer());
                return;
            }

            if (args.Any(a => string.Equals(a, "--self-test", StringComparison.OrdinalIgnoreCase)))
            {
                RunSelfTest(root, reportPath);
                return;
            }

            if (args.Any(a => string.Equals(a, "--test-xml-index", StringComparison.OrdinalIgnoreCase)
                || string.Equals(a, "--test-portable-index", StringComparison.OrdinalIgnoreCase)))
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (var testForm = new MainForm(root)) testForm.RunXmlIndexTest(reportPath);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(root));
        }

        private static string GetArg(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }

        private static string ResolveRoot(string[] args)
        {
            string explicitRoot = GetArg(args, "--root");
            if (!string.IsNullOrWhiteSpace(explicitRoot)) return Path.GetFullPath(explicitRoot);

            string executableFolder = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            if (Directory.Exists(Path.Combine(executableFolder, "Documents"))
                || Directory.Exists(Path.Combine(executableFolder, "Procedures_Modifiables"))) return executableFolder;

            DirectoryInfo parent = Directory.GetParent(executableFolder);
            if (parent != null && (Directory.Exists(Path.Combine(parent.FullName, "Documents"))
                || Directory.Exists(Path.Combine(parent.FullName, "Procedures_Modifiables")))) return parent.FullName;
            return executableFolder;
        }

        private static void RunSelfTest(string root, string reportPath)
        {
            string settingsPath = SettingsStorage.ResolveSettingsPath(root);
            WorkspaceSettings settings = WorkspaceSettings.Load(settingsPath, root);
            var result = new StringBuilder();
            result.AppendLine("Procedure Pilot - autotest");
            result.AppendLine("SettingsFile=" + settingsPath);
            result.AppendLine("Root=" + settings.RootPath);
            result.AppendLine("DocumentsPath=" + settings.DocumentsPath);
            result.AppendLine("PdfPath=" + settings.PdfPath);
            result.AppendLine("ArchivePath=" + settings.ArchivePath);
            result.AppendLine("SettingsFolderPath=" + settings.SettingsFolderPath);
            result.AppendLine("IndexPath=" + settings.IndexPath);
            result.AppendLine("ArchiveIndexPath=" + settings.ArchiveIndexPath);
            result.AppendLine("Language=" + settings.Language);
            result.AppendLine("TagPrefix=" + settings.TagPrefix);
            result.AppendLine("RootExists=" + Directory.Exists(settings.RootPath));
            result.AppendLine("WordFolder=" + Directory.Exists(settings.DocumentsPath));
            result.AppendLine("DocumentCount=" + DocumentSupport.GetDocumentFiles(settings.DocumentsPath).Length);
            result.AppendLine("Index=" + File.Exists(settings.IndexPath));
            result.AppendLine("WordAvailable=" + (Type.GetTypeFromProgID("Word.Application") != null));
            result.AppendLine("LocalPdfEngineAvailable=" + (DocumentSupport.FindLocalPdfRenderer() != null));
            result.AppendLine("LibreOfficeOrOpenOfficeAvailable=" + (DocumentSupport.FindSofficeConverter() != null));
            result.AppendLine("OnlyOfficeAvailable=" + (DocumentSupport.FindOnlyOfficeEditor() != null));
            result.AppendLine("SupportedDocuments=" + string.Join(",", DocumentSupport.Extensions));
            result.AppendLine("IndexFormat=XML");

            string output = result.ToString();
            if (!string.IsNullOrWhiteSpace(reportPath)) File.WriteAllText(reportPath, output, Encoding.UTF8);
        }
    }

    internal sealed class MainForm : Form
    {
        private readonly Color Navy = Color.FromArgb(15, 23, 42);
        private readonly Color Navy2 = Color.FromArgb(30, 41, 59);
        private readonly Color Surface = Color.FromArgb(248, 250, 252);
        private readonly Color Border = Color.FromArgb(226, 232, 240);
        private readonly Color Muted = Color.FromArgb(100, 116, 139);
        private readonly Color Blue = Color.FromArgb(37, 99, 235);
        private readonly Color Green = Color.FromArgb(22, 163, 74);
        private readonly Color Amber = Color.FromArgb(217, 119, 6);

        private string rootPath;
        private string wordFolder;
        private string pdfFolder;
        private string archiveFolder;
        private string indexPath;
        private string archiveIndexPath;
        private string settingsFolder;
        private string settingsPath;
        private string sharedDefaultLanguage = UiText.French;
        private string tagPrefix = ProcedureTag.DefaultPrefix;

        private Label wordCount;
        private Label pdfCount;
        private Label syncCount;
        private Label rootLabel;
        private Label statusLabel;
        private Label resultLabel;
        private ProgressBar progress;
        private TextBox searchBox;
        private TextBox logBox;
        private ListView procedureList;
        private Button runAllButton;
        private Button runAllMenuButton;
        private ContextMenuStrip runAllMenu;
        private readonly List<ProcedureItem> allItems = new List<ProcedureItem>();
        private readonly System.Windows.Forms.Timer documentWatcherTimer;
        private readonly System.Windows.Forms.Timer archiveWatcherTimer;
        private readonly System.Windows.Forms.Timer sharedRefreshTimer;
        private FileSystemWatcher documentWatcher;
        private FileSystemWatcher archiveWatcher;
        private FileSystemWatcher pdfWatcher;
        private FileSystemWatcher dataWatcher;
        private bool documentWatcherPending;
        private bool archiveWatcherPending;
        private bool sharedRefreshPending;
        private string lastDocumentState = "";
        private string lastArchiveState = "";
        private string lastPdfState = "";
        private string lastSharedChangeToken = "";
        private string lastSettingsPath = "";
        private string lastSettingsStamp = "";
        private string activeLogSession;
        private bool logFileErrorDisplayed;
        private static readonly object FileLogLock = new object();
        private bool busy;
        private bool settingsDialogOpen;

        public MainForm(string root)
        {
            settingsPath = SettingsStorage.ResolveSettingsPath(root);
            string initialSettingsFolder = Path.GetDirectoryName(Path.GetFullPath(settingsPath));
            WorkspaceSettings loadedSettings;
            using (WorkspaceWriteLease initializationLease = WorkspaceMultiUser.Acquire(initialSettingsFolder, 30000))
            {
                loadedSettings = WorkspaceSettings.Load(settingsPath, root);
                SettingsStorage.MigrateLegacyLayout(loadedSettings);
                string targetSettingsFolder = Path.GetFullPath(loadedSettings.SettingsFolderPath);
                if (string.Equals(initialSettingsFolder, targetSettingsFolder, StringComparison.OrdinalIgnoreCase))
                {
                    settingsPath = SettingsStorage.Initialize(loadedSettings);
                    WorkspaceMultiUser.PublishChange(targetSettingsFolder, "application-startup");
                }
                else
                {
                    using (WorkspaceWriteLease destinationLease = WorkspaceMultiUser.Acquire(targetSettingsFolder, 30000))
                    {
                        settingsPath = SettingsStorage.Initialize(loadedSettings);
                        WorkspaceMultiUser.PublishChange(targetSettingsFolder, "application-startup");
                    }
                }
            }
            sharedDefaultLanguage = UiText.NormalizeLanguage(loadedSettings.Language);
            string localLanguage = LocalUserPreferences.LoadLanguage(sharedDefaultLanguage);
            loadedSettings.Language = localLanguage;
            ApplyWorkspaceSettings(loadedSettings);

            Text = "Procedure Pilot";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(1040, 680);
            Size = new Size(1220, 790);
            BackColor = Surface;
            Font = new Font("Segoe UI", 9F);
            Icon = CreateAppIcon();

            BuildUi();
            documentWatcherTimer = new System.Windows.Forms.Timer { Interval = 1500 };
            documentWatcherTimer.Tick += async delegate { await ProcessDocumentFolderChangesAsync(); };
            archiveWatcherTimer = new System.Windows.Forms.Timer { Interval = 1500 };
            archiveWatcherTimer.Tick += async delegate { await ProcessArchiveFolderChangesAsync(); };
            sharedRefreshTimer = new System.Windows.Forms.Timer { Interval = 2000 };
            sharedRefreshTimer.Tick += delegate { ProcessSharedRefreshTick(); };
            Shown += delegate { InitializeWorkspace(); };
            FormClosed += delegate
            {
                DisposeWorkspaceWatchers();
                sharedRefreshTimer.Dispose();
                if (runAllMenu != null) runAllMenu.Dispose();
            };
        }

        internal void RunXmlIndexTest(string reportPath)
        {
            try
            {
                int count;
                int archiveCount;
                using (WorkspaceWriteLease lease = WorkspaceMultiUser.Acquire(settingsFolder, 30000))
                {
                    count = UpdateXmlIndex();
                    archiveCount = UpdateArchiveIndex();
                    WorkspaceMultiUser.PublishChange(settingsFolder, "xml-index-test");
                }
                RefreshLibraryWhenStable();
                if (!string.IsNullOrWhiteSpace(reportPath)) File.WriteAllText(reportPath,
                    "OK\r\nProcedures=" + count + "\r\nLibrary=" + allItems.Count
                    + "\r\nArchivedDocuments=" + archiveCount + "\r\nIndex=" + indexPath
                    + "\r\nArchiveIndex=" + archiveIndexPath + "\r\nLanguage=" + UiText.Language
                    + "\r\nTagPrefix=" + tagPrefix, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                if (!string.IsNullOrWhiteSpace(reportPath)) File.WriteAllText(reportPath, "ERROR\r\n" + GetBaseExceptionMessage(ex), Encoding.UTF8);
            }
        }

        private void BuildUi()
        {
            var shell = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 88F));
            shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
            Controls.Add(shell);

            var header = new Panel { Dock = DockStyle.Fill, BackColor = Navy, Padding = new Padding(28, 15, 24, 10), Margin = new Padding(0) };
            shell.Controls.Add(header, 0, 0);

            var logo = new Label
            {
                Text = "P",
                ForeColor = Color.White,
                BackColor = Blue,
                Font = new Font("Segoe UI", 22F, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter,
                Location = new Point(28, 18),
                Size = new Size(52, 52)
            };
            header.Controls.Add(logo);

            var title = new Label
            {
                Text = "PROCEDURE PILOT",
                ForeColor = Color.White,
                Font = new Font("Segoe UI Semibold", 16F, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(96, 18)
            };
            header.Controls.Add(title);

            var subtitle = new Label
            {
                Text = UiText.Get("Centre de gestion documentaire", "Document management center"),
                ForeColor = Color.FromArgb(148, 163, 184),
                Font = new Font("Segoe UI", 9F),
                AutoSize = true,
                Location = new Point(98, 51)
            };
            header.Controls.Add(subtitle);

            statusLabel = new Label
            {
                Text = UiText.Get("PRÊT", "READY"),
                ForeColor = Color.FromArgb(187, 247, 208),
                BackColor = Color.FromArgb(20, 83, 45),
                Font = new Font("Segoe UI Semibold", 8F, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Location = new Point(1080, 26),
                Size = new Size(132, 30)
            };
            header.Controls.Add(statusLabel);

            var settingsButton = NewFlatButton(UiText.Get("PARAMÈTRES", "SETTINGS"), Color.FromArgb(51, 65, 85), Color.White, Color.FromArgb(71, 85, 105));
            settingsButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            settingsButton.Size = new Size(118, 32);
            settingsButton.Location = new Point(948, 25);
            settingsButton.Click += delegate { OpenSettingsDialog(); };
            header.Controls.Add(settingsButton);
            header.Resize += delegate
            {
                statusLabel.Left = header.ClientSize.Width - statusLabel.Width - 28;
                settingsButton.Left = statusLabel.Left - settingsButton.Width - 12;
            };

            var footer = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(24, 7, 24, 0), Margin = new Padding(0) };
            shell.Controls.Add(footer, 0, 2);
            rootLabel = new Label { AutoEllipsis = true, ForeColor = Muted, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
            footer.Controls.Add(rootLabel);

            var content = new Panel { Dock = DockStyle.Fill, BackColor = Surface, Padding = new Padding(26, 20, 26, 20), AutoScroll = true };
            shell.Controls.Add(content, 0, 1);

            var mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                BackColor = Surface,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 96F));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 82F));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            content.Controls.Add(mainLayout);

            var stats = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = Surface, Margin = new Padding(0) };
            stats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333F));
            stats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333F));
            stats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.334F));
            mainLayout.Controls.Add(stats, 0, 0);
            wordCount = AddStatCard(stats, 0, UiText.Get("DOCUMENTS SOURCES", "SOURCE DOCUMENTS"), "0", Blue);
            pdfCount = AddStatCard(stats, 1, UiText.Get("PDF DISPONIBLES", "AVAILABLE PDFS"), "0", Green);
            syncCount = AddStatCard(stats, 2, UiText.Get("PRÊTS À CONSULTER", "READY TO VIEW"), "0", Amber);

            var actionPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(16, 14, 16, 12), Margin = new Padding(0, 0, 0, 12) };
            mainLayout.Controls.Add(actionPanel, 0, 1);

            int buttonX = 16;
            runAllButton = AddUpdateSplitButton(actionPanel, ref buttonX);
            AddActionButton(actionPanel, ref buttonX, UiText.Get("Ajouter un document", "Add a document"), Color.FromArgb(71, 85, 105), async delegate { await AddDocumentAsync(); }, 155);
            AddActionButton(actionPanel, ref buttonX, UiText.Get("Listes de procédures", "Procedure lists"), Color.FromArgb(14, 116, 144), delegate { OpenProcedureLists(); return Task.FromResult(0); }, 165);

            var openFolder = NewFlatButton(UiText.Get("Ouvrir le dossier", "Open folder"), Color.White, Navy2, Border);
            openFolder.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            openFolder.Size = new Size(140, 42);
            openFolder.Location = new Point(actionPanel.ClientSize.Width - 156, 14);
            openFolder.Click += delegate { OpenPath(rootPath); };
            actionPanel.Controls.Add(openFolder);
            actionPanel.Resize += delegate { openFolder.Left = actionPanel.ClientSize.Width - openFolder.Width - 16; };

            var middle = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterDistance = 380,
                SplitterWidth = 8,
                BackColor = Surface,
                Panel1MinSize = 260,
                Panel2MinSize = 100
            };
            mainLayout.Controls.Add(middle, 0, 2);

            var listPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(16, 12, 16, 12) };
            middle.Panel1.Controls.Add(listPanel);

            var listLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                BackColor = Color.White,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            listLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            listLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48F));
            listLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            listPanel.Controls.Add(listLayout);

            var listHeader = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Margin = new Padding(0) };
            listLayout.Controls.Add(listHeader, 0, 0);
            var listTitle = new Label
            {
                Text = UiText.Get("BIBLIOTHÈQUE DES PROCÉDURES", "PROCEDURE LIBRARY"),
                ForeColor = Navy2,
                Font = new Font("Segoe UI Semibold", 10F, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(0, 12)
            };
            listHeader.Controls.Add(listTitle);
            resultLabel = new Label { ForeColor = Muted, AutoSize = true, Location = new Point(225, 13) };
            listHeader.Controls.Add(resultLabel);

            searchBox = new TextBox
            {
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Font = new Font("Segoe UI", 10F),
                BorderStyle = BorderStyle.FixedSingle,
                Size = new Size(280, 30),
                Location = new Point(listHeader.ClientSize.Width - 280, 6)
            };
            searchBox.TextChanged += delegate { ApplyFilter(); };
            listHeader.Controls.Add(searchBox);
            var searchHint = new Label { Text = UiText.Get("Rechercher", "Search"), ForeColor = Muted, AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            listHeader.Controls.Add(searchHint);
            searchHint.Location = new Point(searchBox.Left - searchHint.Width - 10, 11);
            listHeader.Resize += delegate
            {
                searchBox.Left = listHeader.ClientSize.Width - searchBox.Width;
                searchHint.Left = searchBox.Left - searchHint.Width - 10;
                searchHint.Top = 11;
            };

            procedureList = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = false,
                HideSelection = false,
                BorderStyle = BorderStyle.None,
                Font = new Font("Segoe UI", 9.5F),
                BackColor = Color.White,
                ForeColor = Navy2
            };
            procedureList.Columns.Add(UiText.Get("Nom", "Name"), 470);
            procedureList.Columns.Add(UiText.Get("État", "Status"), 150);
            procedureList.Columns.Add(UiText.Get("Modifié", "Modified"), 135);
            procedureList.Columns.Add(UiText.Get("Taille", "Size"), 95, HorizontalAlignment.Right);
            procedureList.DoubleClick += delegate { OpenSelectedPdfOrWord(); };
            listLayout.Controls.Add(procedureList, 0, 1);

            var context = new ContextMenuStrip();
            context.Items.Add(UiText.Get("Ouvrir le PDF", "Open PDF"), null, delegate { OpenSelected(true); });
            context.Items.Add(UiText.Get("Ouvrir le document", "Open document"), null, delegate { OpenSelected(false); });
            context.Items.Add(new ToolStripSeparator());
            context.Items.Add(UiText.Get("Archiver la sélection…", "Archive selection…"), null, async delegate { await ArchiveSelectedAsync(); });
            procedureList.ContextMenuStrip = context;

            var logPanel = new Panel { Dock = DockStyle.Fill, BackColor = Navy, Padding = new Padding(16, 10, 16, 12) };
            middle.Panel2.Controls.Add(logPanel);
            var logTitle = new Label
            {
                Text = UiText.Get("JOURNAL D’ACTIVITÉ", "ACTIVITY LOG"),
                ForeColor = Color.FromArgb(203, 213, 225),
                Font = new Font("Segoe UI Semibold", 8.5F, FontStyle.Bold),
                Dock = DockStyle.Top,
                Height = 25
            };
            logPanel.Controls.Add(logTitle);
            progress = new ProgressBar { Dock = DockStyle.Bottom, Height = 5, Style = ProgressBarStyle.Continuous, Visible = false };
            logPanel.Controls.Add(progress);
            logBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Navy,
                ForeColor = Color.FromArgb(203, 213, 225),
                Font = new Font("Consolas", 9F)
            };
            logPanel.Controls.Add(logBox);
            logPanel.Controls.SetChildIndex(logBox, 1);
        }

        private Label AddStatCard(TableLayoutPanel parent, int column, string title, string value, Color accent)
        {
            var card = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Margin = new Padding(column == 0 ? 0 : 8, 0, column == 2 ? 0 : 8, 14) };
            parent.Controls.Add(card, column, 0);
            var stripe = new Panel { Dock = DockStyle.Left, Width = 5, BackColor = accent };
            card.Controls.Add(stripe);
            var number = new Label
            {
                Text = value,
                ForeColor = Navy,
                Font = new Font("Segoe UI Semibold", 22F, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(24, 13)
            };
            card.Controls.Add(number);
            var caption = new Label
            {
                Text = title,
                ForeColor = Muted,
                Font = new Font("Segoe UI Semibold", 8F, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(26, 53)
            };
            card.Controls.Add(caption);
            return number;
        }

        private Button AddActionButton(Panel panel, ref int x, string text, Color color, Func<Task> handler, int width)
        {
            var button = NewFlatButton(text, color, Color.White, color);
            button.Location = new Point(x, 14);
            button.Size = new Size(width, 42);
            button.Click += async delegate { await handler(); };
            panel.Controls.Add(button);
            x += width + 9;
            return button;
        }

        private Button AddUpdateSplitButton(Panel panel, ref int x)
        {
            const int mainWidth = 180;
            const int arrowWidth = 38;
            var mainButton = NewFlatButton(UiText.Get("TOUT METTRE À JOUR", "UPDATE ALL"), Blue, Color.White, Blue);
            mainButton.Location = new Point(x, 14);
            mainButton.Size = new Size(mainWidth, 42);
            mainButton.Click += async delegate { await RunFullUpdateAsync(); };
            panel.Controls.Add(mainButton);

            runAllMenuButton = NewFlatButton("▼", Color.FromArgb(29, 78, 216), Color.White, Blue);
            runAllMenuButton.AccessibleName = UiText.Get("Actions supplémentaires de mise à jour", "Additional update actions");
            runAllMenuButton.Font = new Font("Segoe UI", 8F, FontStyle.Bold);
            runAllMenuButton.Location = new Point(x + mainWidth - 1, 14);
            runAllMenuButton.Size = new Size(arrowWidth, 42);
            panel.Controls.Add(runAllMenuButton);

            runAllMenu = new ContextMenuStrip
            {
                Font = new Font("Segoe UI", 9F),
                ShowImageMargin = false,
                ShowCheckMargin = false
            };
            runAllMenu.Items.Add(UiText.Get("Mise à jour des tags", "Update tags"), null, async delegate { await RunRenameAsync(); });
            runAllMenu.Items.Add(UiText.Get("Convertir en PDF", "Convert to PDF"), null, async delegate { await RunConvertAsync(); });
            runAllMenu.Items.Add(UiText.Get("Actualiser les index XML", "Refresh XML indexes"), null, async delegate { await RunIndexAsync(); });
            runAllMenuButton.Click += delegate
            {
                if (!busy) runAllMenu.Show(runAllMenuButton, new Point(0, runAllMenuButton.Height));
            };

            x += mainWidth + arrowWidth + 8;
            return mainButton;
        }

        private Button NewFlatButton(string text, Color background, Color foreground, Color border)
        {
            var button = new Button
            {
                Text = text,
                BackColor = background,
                ForeColor = foreground,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI Semibold", 8.5F, FontStyle.Bold),
                UseVisualStyleBackColor = false
            };
            button.FlatAppearance.BorderColor = border;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.MouseOverBackColor = ControlPaint.Light(background, 0.08F);
            return button;
        }

        private void InitializeWorkspace()
        {
            DisposeWorkspaceWatchers();
            rootLabel.Text = UiText.Get("Dossier actif  •  ", "Active folder  •  ") + rootPath;
            if (!Directory.Exists(wordFolder))
            {
                DialogResult configure = MessageBox.Show(
                    UiText.Get(
                        "Le dossier des procédures modifiables est introuvable :\r\n\r\n" + wordFolder + "\r\n\r\nVoulez-vous configurer les emplacements maintenant ?",
                        "The editable procedures folder could not be found:\r\n\r\n" + wordFolder + "\r\n\r\nWould you like to configure the locations now?"),
                    UiText.Get("Dossier de procédures introuvable", "Procedure folder not found"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                SetStatus(UiText.Get("DOSSIER INVALIDE", "INVALID FOLDER"), Color.FromArgb(127, 29, 29), Color.FromArgb(254, 202, 202));
                if (configure == DialogResult.Yes) OpenSettingsDialog();
                return;
            }
            Directory.CreateDirectory(pdfFolder);
            Directory.CreateDirectory(archiveFolder);
            Directory.CreateDirectory(settingsFolder);
            Log(UiText.Get("Espace de travail chargé : ", "Workspace loaded: ") + rootPath);
            try
            {
                WorkspaceWriteLease startupLease = WorkspaceMultiUser.TryAcquire(settingsFolder, 1500);
                if (startupLease != null)
                {
                    using (startupLease)
                    {
                        UpdateXmlIndex();
                        UpdateArchiveIndex();
                        SettingsStorage.CleanupLegacyXmlFiles(CurrentWorkspaceSettings());
                        WorkspaceMultiUser.PublishChange(settingsFolder, "startup-index");
                    }
                }
                else Log(UiText.Get(
                    "Un autre poste actualise déjà l’espace partagé ; les index seront repris automatiquement.",
                    "Another computer is already updating the shared workspace; indexes will refresh automatically."));
            }
            catch (Exception ex) { Log(UiText.Get("Impossible de créer les index XML : ", "Unable to create XML indexes: ") + GetBaseExceptionMessage(ex)); }
            string localPdfRenderer = DocumentSupport.FindLocalPdfRenderer();
            if (localPdfRenderer != null) Log(UiText.Get(
                "Conversion PDF locale intégrée disponible (hors ligne, sans Word ni ONLYOFFICE).",
                "Built-in local PDF conversion is available (offline, without Word or ONLYOFFICE)."));
            string portableOffice = DocumentSupport.FindSofficeConverter();
            if (portableOffice != null) Log(UiText.Get("Conversion portable disponible : ", "Portable conversion available: ") + DocumentSupport.GetOfficeSuiteName(portableOffice) + ".");
            else if (DocumentSupport.FindOnlyOfficeEditor() != null) Log(UiText.Get(
                "ONLYOFFICE détecté : les documents compatibles sont reconnus et s’ouvrent avec l’application associée.",
                "ONLYOFFICE detected: compatible documents are recognized and open with their associated application."));
            RefreshLibraryWhenStable();
            StartDocumentWatcher();
            StartArchiveWatcher();
            StartSharedRefreshMonitoring();
            Log(UiText.Get(
                "Mode multi-utilisateur NAS actif : verrouillage partagé et actualisation réseau toutes les 2 secondes.",
                "NAS multi-user mode active: shared locking and network refresh every 2 seconds."));
        }

        private WorkspaceSettings CurrentWorkspaceSettings()
        {
            return new WorkspaceSettings
            {
                RootPath = rootPath,
                DocumentsPath = wordFolder,
                PdfPath = pdfFolder,
                ArchivePath = archiveFolder,
                SettingsFolderPath = settingsFolder,
                IndexPath = indexPath,
                ArchiveIndexPath = archiveIndexPath,
                Language = UiText.Language,
                TagPrefix = tagPrefix
            };
        }

        private void StartDocumentWatcher()
        {
            DisposeDocumentWatcher();
            if (!Directory.Exists(wordFolder)) return;

            lastDocumentState = CaptureDocumentState();
            documentWatcher = new FileSystemWatcher(wordFolder)
            {
                Filter = "*.*",
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                SynchronizingObject = this,
                InternalBufferSize = 32768
            };
            documentWatcher.Created += WatchedDocumentChanged;
            documentWatcher.Changed += WatchedDocumentChanged;
            documentWatcher.Deleted += WatchedDocumentChanged;
            documentWatcher.Renamed += WatchedDocumentRenamed;
            documentWatcher.Error += DocumentWatcherError;
            documentWatcher.EnableRaisingEvents = true;
            Log(UiText.Get(
                "Surveillance automatique activée : les ajouts, modifications et suppressions sont détectés.",
                "Automatic monitoring enabled: additions, changes, and deletions are detected."));
        }

        private void DisposeDocumentWatcher()
        {
            documentWatcherTimer.Stop();
            documentWatcherPending = false;
            if (documentWatcher == null) return;
            documentWatcher.EnableRaisingEvents = false;
            documentWatcher.Dispose();
            documentWatcher = null;
        }

        private void StartArchiveWatcher()
        {
            DisposeArchiveWatcher();
            if (!Directory.Exists(archiveFolder)) return;

            lastArchiveState = CaptureArchiveState();
            archiveWatcher = new FileSystemWatcher(archiveFolder)
            {
                Filter = "*.*",
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                SynchronizingObject = this,
                InternalBufferSize = 32768
            };
            archiveWatcher.Created += WatchedArchiveChanged;
            archiveWatcher.Changed += WatchedArchiveChanged;
            archiveWatcher.Deleted += WatchedArchiveChanged;
            archiveWatcher.Renamed += WatchedArchiveChanged;
            archiveWatcher.Error += ArchiveWatcherError;
            archiveWatcher.EnableRaisingEvents = true;
        }

        private void StartSharedRefreshMonitoring()
        {
            DisposeSharedRefreshMonitoring();
            lastPdfState = CapturePdfState();
            lastSharedChangeToken = WorkspaceMultiUser.ReadChangeToken(settingsFolder);
            lastSettingsPath = settingsPath;
            lastSettingsStamp = WorkspaceMultiUser.GetFileStamp(settingsPath);

            if (Directory.Exists(pdfFolder))
            {
                pdfWatcher = new FileSystemWatcher(pdfFolder)
                {
                    Filter = "*.pdf",
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                    SynchronizingObject = this,
                    InternalBufferSize = 32768
                };
                pdfWatcher.Created += WatchedSharedStateChanged;
                pdfWatcher.Changed += WatchedSharedStateChanged;
                pdfWatcher.Deleted += WatchedSharedStateChanged;
                pdfWatcher.Renamed += WatchedSharedStateChanged;
                pdfWatcher.Error += SharedWatcherError;
                pdfWatcher.EnableRaisingEvents = true;
            }

            if (Directory.Exists(settingsFolder))
            {
                dataWatcher = new FileSystemWatcher(settingsFolder)
                {
                    Filter = "*",
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                    SynchronizingObject = this,
                    InternalBufferSize = 32768
                };
                dataWatcher.Created += WatchedSharedStateChanged;
                dataWatcher.Changed += WatchedSharedStateChanged;
                dataWatcher.Deleted += WatchedSharedStateChanged;
                dataWatcher.Renamed += WatchedSharedStateChanged;
                dataWatcher.Error += SharedWatcherError;
                dataWatcher.EnableRaisingEvents = true;
            }

            sharedRefreshPending = false;
            sharedRefreshTimer.Start();
        }

        private void DisposeArchiveWatcher()
        {
            archiveWatcherTimer.Stop();
            archiveWatcherPending = false;
            if (archiveWatcher == null) return;
            archiveWatcher.EnableRaisingEvents = false;
            archiveWatcher.Dispose();
            archiveWatcher = null;
        }

        private void DisposeSharedRefreshMonitoring()
        {
            sharedRefreshTimer.Stop();
            sharedRefreshPending = false;
            if (pdfWatcher != null)
            {
                pdfWatcher.EnableRaisingEvents = false;
                pdfWatcher.Dispose();
                pdfWatcher = null;
            }
            if (dataWatcher != null)
            {
                dataWatcher.EnableRaisingEvents = false;
                dataWatcher.Dispose();
                dataWatcher = null;
            }
        }

        private void DisposeWorkspaceWatchers()
        {
            DisposeDocumentWatcher();
            DisposeArchiveWatcher();
            DisposeSharedRefreshMonitoring();
        }

        private void WatchedSharedStateChanged(object sender, FileSystemEventArgs e)
        {
            if (ReferenceEquals(sender, dataWatcher))
            {
                string name = Path.GetFileName(e.FullPath) ?? "";
                if (!string.Equals(name, WorkspaceMultiUser.ChangeFileName, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(name, "Index.xml", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(name, "Archive.xml", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(name, ProcedureListStorage.FileName, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(name, "Settings.xml", StringComparison.OrdinalIgnoreCase)) return;
            }
            sharedRefreshPending = true;
        }

        private void SharedWatcherError(object sender, ErrorEventArgs e)
        {
            LogSystem(UiText.Get(
                "La surveillance des données partagées a été réinitialisée après une erreur réseau.",
                "Shared-data monitoring was reset after a network error."));
            StartSharedRefreshMonitoring();
            sharedRefreshPending = true;
        }

        private void ProcessSharedRefreshTick()
        {
            if (busy || settingsDialogOpen) return;
            if (ReloadSharedSettingsIfChanged()) return;

            string currentDocumentState = CaptureDocumentState();
            if (IsReadableCapturedState(currentDocumentState)
                && !string.Equals(currentDocumentState, lastDocumentState, StringComparison.Ordinal))
                ScheduleDocumentFolderSynchronization();

            string currentArchiveState = CaptureArchiveState();
            if (IsReadableCapturedState(currentArchiveState)
                && !string.Equals(currentArchiveState, lastArchiveState, StringComparison.Ordinal))
            {
                archiveWatcherPending = true;
                archiveWatcherTimer.Stop();
                archiveWatcherTimer.Start();
            }

            string currentPdfState = CapturePdfState();
            string currentToken = WorkspaceMultiUser.ReadChangeToken(settingsFolder);
            if (!sharedRefreshPending
                && (!IsReadableCapturedState(currentPdfState) || string.Equals(currentPdfState, lastPdfState, StringComparison.Ordinal))
                && string.Equals(currentToken, lastSharedChangeToken, StringComparison.Ordinal)) return;

            if (!RefreshLibraryWhenStable()) return;
            lastPdfState = currentPdfState;
            lastSharedChangeToken = currentToken;
            sharedRefreshPending = false;
        }

        private bool ReloadSharedSettingsIfChanged()
        {
            WorkspaceSettings refreshed;
            string resolvedPath;
            string currentStamp;
            WorkspaceWriteLease settingsReadLease = WorkspaceMultiUser.TryAcquire(settingsFolder, 0);
            if (settingsReadLease == null) return false;
            using (settingsReadLease)
            {
                try { resolvedPath = SettingsStorage.ResolveSettingsPath(rootPath); }
                catch { return false; }
                currentStamp = WorkspaceMultiUser.GetFileStamp(resolvedPath);
                if (string.Equals(resolvedPath, lastSettingsPath, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(currentStamp, lastSettingsStamp, StringComparison.Ordinal)) return false;
                if (string.IsNullOrWhiteSpace(currentStamp) || !File.Exists(resolvedPath)) return false;
                refreshed = WorkspaceSettings.Load(resolvedPath, rootPath);
                lastSettingsPath = resolvedPath;
                lastSettingsStamp = currentStamp;
            }

            sharedDefaultLanguage = UiText.NormalizeLanguage(refreshed.Language);
            refreshed.Language = LocalUserPreferences.LoadLanguage(UiText.Language);

            bool locationsChanged = !string.Equals(rootPath, refreshed.RootPath, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(wordFolder, refreshed.DocumentsPath, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(pdfFolder, refreshed.PdfPath, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(archiveFolder, refreshed.ArchivePath, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(settingsFolder, refreshed.SettingsFolderPath, StringComparison.OrdinalIgnoreCase);
            bool tagPrefixChanged = !string.Equals(tagPrefix, refreshed.TagPrefix, StringComparison.OrdinalIgnoreCase);
            if (!locationsChanged && !tagPrefixChanged) return false;

            settingsPath = resolvedPath;
            ApplyWorkspaceSettings(refreshed);
            LogSystem(UiText.Get(
                "Paramètres partagés modifiés par un autre poste ; rechargement automatique.",
                "Shared settings were changed by another computer; reloading automatically."));
            if (locationsChanged) InitializeWorkspace();
            return true;
        }

        private static bool IsReadableCapturedState(string value)
        {
            return value != null && (value.Length == 0 || value[0] != '<');
        }

        private void WatchedArchiveChanged(object sender, FileSystemEventArgs e)
        {
            archiveWatcherPending = true;
            archiveWatcherTimer.Stop();
            archiveWatcherTimer.Start();
        }

        private void ArchiveWatcherError(object sender, ErrorEventArgs e)
        {
            LogSystem(UiText.Get("La surveillance des archives a été réinitialisée après une erreur.", "Archive monitoring was reset after an error."));
            StartArchiveWatcher();
            archiveWatcherPending = true;
            archiveWatcherTimer.Start();
        }

        private async Task ProcessArchiveFolderChangesAsync()
        {
            archiveWatcherTimer.Stop();
            if (!archiveWatcherPending) return;
            if (busy || settingsDialogOpen)
            {
                archiveWatcherTimer.Start();
                return;
            }

            archiveWatcherPending = false;
            string currentState = CaptureArchiveState();
            if (string.Equals(currentState, lastArchiveState, StringComparison.Ordinal)) return;
            if (!BeginOperation("ARCHIVE"))
            {
                archiveWatcherPending = true;
                archiveWatcherTimer.Start();
                return;
            }
            try
            {
                int count = await Task.Run(delegate
                {
                    WorkspaceWriteLease lease = WorkspaceMultiUser.TryAcquire(settingsFolder, 750);
                    if (lease == null) return -1;
                    using (lease)
                    {
                        int updated = UpdateArchiveIndex();
                        WorkspaceMultiUser.PublishChange(settingsFolder, "archive-index");
                        return updated;
                    }
                });
                if (count < 0)
                {
                    archiveWatcherPending = true;
                    LogSystem(UiText.Get(
                        "Actualisation de l’archive différée : un autre poste écrit dans l’espace partagé.",
                        "Archive refresh postponed: another computer is writing to the shared workspace."));
                }
                else
                {
                    lastArchiveState = CaptureArchiveState();
                    LogSystem(UiText.Format(
                        "Archive.xml actualisé automatiquement avec {0} document(s) modifiable(s).",
                        "Archive.xml automatically refreshed with {0} editable document(s).", count));
                }
            }
            catch (Exception ex)
            {
                archiveWatcherPending = true;
                LogSystem(UiText.Get("Actualisation automatique de l’archive impossible : ", "Unable to refresh the archive automatically: ") + GetBaseExceptionMessage(ex));
            }
            finally
            {
                EndOperation();
                if (archiveWatcherPending) archiveWatcherTimer.Start();
            }
        }

        private void WatchedDocumentChanged(object sender, FileSystemEventArgs e)
        {
            if (!IsWatchedDocument(e.FullPath)) return;
            ScheduleDocumentFolderSynchronization();
        }

        private void WatchedDocumentRenamed(object sender, RenamedEventArgs e)
        {
            if (!IsWatchedDocument(e.FullPath) && !IsWatchedDocument(e.OldFullPath)) return;
            ScheduleDocumentFolderSynchronization();
        }

        private void DocumentWatcherError(object sender, ErrorEventArgs e)
        {
            LogSystem(UiText.Get("La surveillance du dossier a été réinitialisée après une erreur.", "Folder monitoring was reset after an error."));
            StartDocumentWatcher();
            ScheduleDocumentFolderSynchronization();
        }

        private static bool IsWatchedDocument(string path)
        {
            string name = Path.GetFileName(path) ?? "";
            return !name.StartsWith("~$", StringComparison.OrdinalIgnoreCase) && DocumentSupport.IsSupported(path);
        }

        private void ScheduleDocumentFolderSynchronization()
        {
            documentWatcherPending = true;
            documentWatcherTimer.Stop();
            documentWatcherTimer.Start();
        }

        private async Task ProcessDocumentFolderChangesAsync()
        {
            documentWatcherTimer.Stop();
            if (!documentWatcherPending) return;
            if (busy || settingsDialogOpen)
            {
                documentWatcherTimer.Start();
                return;
            }

            documentWatcherPending = false;
            string currentState = CaptureDocumentState();
            if (string.Equals(currentState, lastDocumentState, StringComparison.Ordinal)) return;

            LogSystem(UiText.Get(
                "Changement manuel détecté dans le dossier des procédures. Synchronisation automatique…",
                "Manual change detected in the procedures folder. Starting automatic synchronization…"));
            await RunFullUpdateCoreAsync(true);
        }

        private string CaptureDocumentState()
        {
            if (!Directory.Exists(wordFolder)) return "<dossier-absent>";
            try
            {
                var state = new StringBuilder();
                foreach (string path in DocumentSupport.GetDocumentFiles(wordFolder))
                {
                    var info = new FileInfo(path);
                    state.Append(info.Name.ToUpperInvariant())
                        .Append('|').Append(info.Length)
                        .Append('|').Append(info.LastWriteTimeUtc.Ticks)
                        .AppendLine();
                }
                return state.ToString();
            }
            catch (IOException) { return "<lecture-en-cours>"; }
            catch (UnauthorizedAccessException) { return "<acces-refuse>"; }
        }

        private string CaptureArchiveState()
        {
            if (!Directory.Exists(archiveFolder)) return "<dossier-absent>";
            try
            {
                var state = new StringBuilder();
                foreach (string path in DocumentSupport.GetDocumentFiles(archiveFolder, true))
                {
                    var info = new FileInfo(path);
                    state.Append(path.Substring(archiveFolder.TrimEnd(Path.DirectorySeparatorChar).Length).ToUpperInvariant())
                        .Append('|').Append(info.Length)
                        .Append('|').Append(info.LastWriteTimeUtc.Ticks)
                        .AppendLine();
                }
                return state.ToString();
            }
            catch (IOException) { return "<lecture-en-cours>"; }
            catch (UnauthorizedAccessException) { return "<acces-refuse>"; }
        }

        private string CapturePdfState()
        {
            if (!Directory.Exists(pdfFolder)) return "<dossier-absent>";
            try
            {
                var state = new StringBuilder();
                foreach (string path in Directory.GetFiles(pdfFolder, "*.pdf").OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                {
                    var info = new FileInfo(path);
                    state.Append(info.Name.ToUpperInvariant())
                        .Append('|').Append(info.Length)
                        .Append('|').Append(info.LastWriteTimeUtc.Ticks)
                        .AppendLine();
                }
                return state.ToString();
            }
            catch (IOException) { return "<lecture-en-cours>"; }
            catch (UnauthorizedAccessException) { return "<acces-refuse>"; }
        }

        private void ApplyWorkspaceSettings(WorkspaceSettings settings)
        {
            UiText.SetLanguage(settings.Language);
            rootPath = settings.RootPath;
            wordFolder = settings.DocumentsPath;
            pdfFolder = settings.PdfPath;
            archiveFolder = settings.ArchivePath;
            indexPath = settings.IndexPath;
            archiveIndexPath = settings.ArchiveIndexPath;
            settingsFolder = settings.SettingsFolderPath;
            tagPrefix = ProcedureTag.NormalizePrefixOrDefault(settings.TagPrefix);
        }

        private void OpenSettingsDialog()
        {
            if (busy) return;
            settingsDialogOpen = true;
            try
            {
                var current = new WorkspaceSettings
                {
                    RootPath = rootPath,
                    DocumentsPath = wordFolder,
                    PdfPath = pdfFolder,
                    ArchivePath = archiveFolder,
                    IndexPath = indexPath,
                    ArchiveIndexPath = archiveIndexPath,
                    SettingsFolderPath = settingsFolder,
                    Language = UiText.Language,
                    TagPrefix = tagPrefix
                };

                using (var dialog = new SettingsForm(current))
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    try
                    {
                        WorkspaceSettings selected = dialog.SelectedSettings;
                        string selectedUserLanguage = UiText.NormalizeLanguage(selected.Language);
                        string previousDataFolder = settingsFolder;
                        string previousLanguage = UiText.Language;
                        WorkspaceWriteLease settingsLease = WorkspaceMultiUser.TryAcquire(previousDataFolder, 3000);
                        if (settingsLease == null)
                        {
                            MessageBox.Show(
                                UiText.Get(
                                    "Un autre poste utilise actuellement les paramètres partagés. Réessayez dans quelques instants.",
                                    "Another computer is currently using the shared settings. Try again in a moment."),
                                UiText.Get("Paramètres occupés", "Settings busy"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                            return;
                        }
                        bool dataFolderChanged = !string.Equals(
                            Path.GetFullPath(previousDataFolder),
                            Path.GetFullPath(selected.SettingsFolderPath),
                            StringComparison.OrdinalIgnoreCase);
                        bool destinationDataFolderBusy = false;
                        using (settingsLease)
                        {
                            WorkspaceWriteLease destinationDataLease = dataFolderChanged
                                ? WorkspaceMultiUser.TryAcquire(selected.SettingsFolderPath, 3000)
                                : null;
                            destinationDataFolderBusy = dataFolderChanged && destinationDataLease == null;
                            if (!destinationDataFolderBusy)
                            {
                                using (destinationDataLease)
                                {
                                    Directory.CreateDirectory(selected.RootPath);
                                    Directory.CreateDirectory(selected.DocumentsPath);
                                    Directory.CreateDirectory(selected.PdfPath);
                                    Directory.CreateDirectory(selected.ArchivePath);
                                    Directory.CreateDirectory(selected.SettingsFolderPath);
                                    ProcedureListStorage.CopyToNewDataFolder(previousDataFolder, selected.SettingsFolderPath);
                                    LocalUserPreferences.SaveLanguage(selectedUserLanguage);
                                    selected.Language = sharedDefaultLanguage;
                                    settingsPath = SettingsStorage.Store(selected);
                                    WorkspaceMultiUser.PublishChange(selected.SettingsFolderPath, "settings");
                                    if (dataFolderChanged)
                                        WorkspaceMultiUser.PublishChange(previousDataFolder, "settings-location");
                                }
                            }
                        }
                        if (destinationDataFolderBusy)
                        {
                            MessageBox.Show(
                                UiText.Get(
                                    "Le nouveau dossier des données est actuellement utilisé par un autre poste. Réessayez dans quelques instants.",
                                    "The new data folder is currently in use by another computer. Try again in a moment."),
                                UiText.Get("Paramètres occupés", "Settings busy"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                            return;
                        }
                        selected.Language = selectedUserLanguage;
                        ApplyWorkspaceSettings(selected);
                        if (!string.Equals(previousLanguage, UiText.Language, StringComparison.Ordinal)) RebuildUi();
                        LogUser(UiText.Get("Paramètres enregistrés.", "Settings saved."));
                        LogUser(UiText.Get("Documents : ", "Documents: ") + wordFolder);
                        LogUser("PDF : " + pdfFolder);
                        LogUser(UiText.Get("Données de l’application : ", "Application data: ") + settingsFolder);
                        LogUser(UiText.Get("Préfixe des tags : ", "Tag prefix: ") + tagPrefix
                            + " (" + ProcedureTag.FormatCode(tagPrefix, 1) + ")");
                        LogUser("XML : " + indexPath + " | " + settingsPath + " | " + archiveIndexPath);
                        InitializeWorkspace();
                        WorkspaceWriteLease cleanupLease = WorkspaceMultiUser.TryAcquire(previousDataFolder, 1000);
                        if (cleanupLease != null)
                        {
                            using (cleanupLease)
                                SettingsStorage.CleanupSupersededDataFolder(previousDataFolder, CurrentWorkspaceSettings());
                        }
                    }
                    catch (Exception ex)
                    {
                        LogUser(UiText.Get("ERREUR lors de l’enregistrement des paramètres : ", "ERROR while saving settings: ") + GetBaseExceptionMessage(ex));
                        MessageBox.Show(GetBaseExceptionMessage(ex), UiText.Get("Impossible d’enregistrer les paramètres", "Unable to save settings"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
            finally
            {
                settingsDialogOpen = false;
                if (!busy && documentWatcherPending) documentWatcherTimer.Start();
                if (!busy && archiveWatcherPending) archiveWatcherTimer.Start();
            }
        }

        private void RebuildUi()
        {
            DisposeWorkspaceWatchers();
            if (runAllMenu != null)
            {
                runAllMenu.Dispose();
                runAllMenu = null;
            }

            Control[] previousControls = Controls.Cast<Control>().ToArray();
            Controls.Clear();
            foreach (Control control in previousControls) control.Dispose();
            BuildUi();
        }

        private void RefreshLibrary()
        {
            allItems.Clear();
            var byName = new Dictionary<string, ProcedureItem>(StringComparer.OrdinalIgnoreCase);

            if (Directory.Exists(wordFolder))
            {
                foreach (string path in DocumentSupport.GetDocumentFiles(wordFolder))
                {
                    string key = Path.GetFileNameWithoutExtension(path);
                    ProcedureItem item;
                    if (!byName.TryGetValue(key, out item))
                    {
                        item = new ProcedureItem { Name = key };
                        byName.Add(key, item);
                    }
                    if (item.WordPath == null || DocumentSupport.GetPreference(path) < DocumentSupport.GetPreference(item.WordPath))
                        item.WordPath = path;
                }
            }

            if (Directory.Exists(pdfFolder))
            {
                foreach (string path in Directory.GetFiles(pdfFolder, "*.pdf"))
                {
                    string key = Path.GetFileNameWithoutExtension(path);
                    ProcedureItem item;
                    if (!byName.TryGetValue(key, out item)) continue;
                    item.PdfPath = path;
                }
            }

            allItems.AddRange(byName.Values.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase));
            ApplyFilter();

            int words = Directory.Exists(wordFolder) ? DocumentSupport.GetDocumentFiles(wordFolder).Length : 0;
            int pdfs = allItems.Count(i => i.PdfPath != null);
            int ready = allItems.Count(i => DocumentSupport.IsPdfCurrent(i.WordPath, i.PdfPath));
            wordCount.Text = words.ToString();
            pdfCount.Text = pdfs.ToString();
            syncCount.Text = ready.ToString();
            SetStatus(UiText.Get("PRÊT", "READY"), Color.FromArgb(20, 83, 45), Color.FromArgb(187, 247, 208));
        }

        private bool RefreshLibraryWhenStable()
        {
            WorkspaceWriteLease lease = WorkspaceMultiUser.TryAcquire(settingsFolder, 0);
            if (lease == null)
            {
                sharedRefreshPending = true;
                return false;
            }
            using (lease)
            {
                try
                {
                    RefreshLibrary();
                    return true;
                }
                catch (IOException)
                {
                    sharedRefreshPending = true;
                    return false;
                }
                catch (UnauthorizedAccessException)
                {
                    sharedRefreshPending = true;
                    return false;
                }
            }
        }

        private void ApplyFilter()
        {
            if (procedureList == null) return;
            string query = searchBox == null ? "" : searchBox.Text.Trim();
            procedureList.BeginUpdate();
            int count = 0;
            try
            {
                procedureList.Items.Clear();
                IEnumerable<ProcedureItem> items = allItems;
                if (query.Length > 0) items = items.Where(i => i.Name.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0);

                foreach (ProcedureItem item in items)
                {
                    try
                    {
                        if (string.IsNullOrWhiteSpace(item.WordPath) || !File.Exists(item.WordPath)) continue;
                        bool pdfCurrent = DocumentSupport.IsPdfCurrent(item.WordPath, item.PdfPath);
                        string state = pdfCurrent
                            ? UiText.Get("Synchronisé", "Synchronized")
                            : item.PdfPath != null
                                ? UiText.Get("PDF à actualiser", "PDF needs updating")
                                : UiText.Get("PDF à générer", "PDF to generate");
                        string newest = item.PdfPath != null && File.Exists(item.PdfPath)
                            && File.GetLastWriteTimeUtc(item.PdfPath) >= File.GetLastWriteTimeUtc(item.WordPath)
                                ? item.PdfPath
                                : item.WordPath;
                        var info = new FileInfo(newest);
                        string size = FormatSize(info.Length);
                        var row = new ListViewItem(new[]
                        {
                            item.Name,
                            state,
                            info.LastWriteTime.ToString(UiText.IsEnglish ? "MM/dd/yyyy HH:mm" : "dd/MM/yyyy HH:mm"),
                            size
                        });
                        row.Tag = item;
                        if (!pdfCurrent) row.ForeColor = Amber;
                        procedureList.Items.Add(row);
                        count++;
                    }
                    catch (IOException)
                    {
                        // Un autre poste peut déplacer ou remplacer le fichier pendant ce rafraîchissement.
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // Le prochain signal partagé ou passage du polling reconstruira la bibliothèque.
                    }
                }
            }
            finally
            {
                procedureList.EndUpdate();
            }
            resultLabel.Text = UiText.IsEnglish
                ? count + (count == 1 ? " item" : " items")
                : count + (count == 1 ? " élément" : " éléments");
        }

        private async Task RunFullUpdateAsync()
        {
            await RunFullUpdateCoreAsync(false);
        }

        private async Task RunFullUpdateCoreAsync(bool automatic)
        {
            if (!BeginOperation(automatic
                ? UiText.Get("SYNCHRONISATION", "SYNCHRONIZING")
                : UiText.Get("MISE À JOUR", "UPDATING"))) return;
            if (!automatic) StartUserLogContext(UiText.Get("Mise à jour complète demandée.", "Full update requested."));
            bool sharedUpdateExecuted = false;
            bool sharedUpdateDeferred = false;
            try
            {
                await Task.Run(delegate
                {
                    WorkspaceWriteLease lease = automatic
                        ? WorkspaceMultiUser.TryAcquire(settingsFolder, 750)
                        : AcquireSharedWriteForUser();
                    if (lease == null)
                    {
                        sharedUpdateDeferred = true;
                        return;
                    }
                    using (lease)
                    {
                        sharedUpdateExecuted = true;
                        RenameDocuments();
                        Exception conversionError = null;
                        try { ConvertDocuments(); }
                        catch (Exception ex)
                        {
                            conversionError = ex;
                            Log(UiText.Get("Conversion non exécutée : ", "Conversion not completed: ") + GetBaseExceptionMessage(ex));
                        }
                        UpdateXmlIndex();
                        UpdateArchiveIndex();
                        WorkspaceMultiUser.PublishChange(settingsFolder, automatic ? "automatic-full-update" : "full-update");
                        if (conversionError != null)
                            throw new InvalidOperationException(UiText.Get(
                                "Les index ont été mis à jour, mais la conversion PDF n’a pas pu être terminée :\r\n\r\n",
                                "The indexes were updated, but PDF conversion could not be completed:\r\n\r\n") + GetBaseExceptionMessage(conversionError));
                    }
                });
                if (sharedUpdateDeferred)
                {
                    Log(UiText.Get(
                        "Synchronisation différée : un autre poste met déjà l’espace partagé à jour.",
                        "Synchronization postponed: another computer is already updating the shared workspace."));
                    return;
                }
                Log(automatic
                    ? UiText.Get("Synchronisation automatique terminée.", "Automatic synchronization completed.")
                    : UiText.Get("Mise à jour complète terminée.", "Full update completed."));
                if (!automatic)
                    MessageBox.Show(
                        UiText.Get("Les procédures et les index sont à jour.", "The procedures and indexes are up to date."),
                        UiText.Get("Mise à jour terminée", "Update completed"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                if (automatic) Log(UiText.Get("Synchronisation automatique incomplète : ", "Automatic synchronization incomplete: ") + GetBaseExceptionMessage(ex));
                else ShowOperationError(ex);
            }
            finally
            {
                if (sharedUpdateExecuted)
                {
                    lastDocumentState = CaptureDocumentState();
                    lastArchiveState = CaptureArchiveState();
                    lastPdfState = CapturePdfState();
                    lastSharedChangeToken = WorkspaceMultiUser.ReadChangeToken(settingsFolder);
                }
                else if (automatic) documentWatcherPending = true;
                if (!automatic) EndUserLogContext();
                EndOperation();
                if (documentWatcherPending) documentWatcherTimer.Start();
            }
        }

        private async Task RunRenameAsync()
        {
            if (!BeginUserOperation(UiText.Get("MAJ TAGS", "UPDATING TAGS"), UiText.Get("Mise à jour des tags demandée.", "Tag update requested."))) return;
            try { await Task.Run(delegate { ExecuteSharedWrite(RenameDocuments, "rename-documents"); }); }
            catch (Exception ex) { ShowOperationError(ex); }
            finally
            {
                EndUserLogContext();
                EndOperation();
            }
        }

        private async Task RunConvertAsync()
        {
            if (!BeginUserOperation(UiText.Get("CONVERSION", "CONVERTING"), UiText.Get("Conversion PDF demandée.", "PDF conversion requested."))) return;
            try { await Task.Run(delegate { ExecuteSharedWrite(ConvertDocuments, "convert-pdf"); }); }
            catch (Exception ex) { ShowOperationError(ex); }
            finally
            {
                EndUserLogContext();
                EndOperation();
            }
        }

        private async Task RunIndexAsync()
        {
            if (!BeginUserOperation(UiText.Get("INDEX XML", "XML INDEX"), UiText.Get("Actualisation des index XML demandée.", "XML index refresh requested."))) return;
            try
            {
                await Task.Run(delegate
                {
                    ExecuteSharedWrite(delegate
                    {
                        UpdateXmlIndex();
                        UpdateArchiveIndex();
                        SettingsStorage.CleanupLegacyXmlFiles(CurrentWorkspaceSettings());
                    }, "refresh-indexes");
                });
            }
            catch (Exception ex) { ShowOperationError(ex); }
            finally
            {
                lastArchiveState = CaptureArchiveState();
                EndUserLogContext();
                EndOperation();
            }
        }

        private void ExecuteSharedWrite(Action action, string changeKind)
        {
            using (WorkspaceWriteLease lease = AcquireSharedWriteForUser())
            {
                action();
                WorkspaceMultiUser.PublishChange(settingsFolder, changeKind);
            }
        }

        private WorkspaceWriteLease AcquireSharedWriteForUser()
        {
            WorkspaceWriteLease lease = WorkspaceMultiUser.TryAcquire(settingsFolder, 0);
            if (lease != null) return lease;
            Log(UiText.Get(
                "Un autre poste écrit dans l’espace partagé ; attente du verrou NAS…",
                "Another computer is writing to the shared workspace; waiting for the NAS lock…"));
            SetStatus(UiText.Get("ATTENTE NAS", "WAITING FOR NAS"), Color.FromArgb(88, 28, 135), Color.FromArgb(243, 232, 255));
            return WorkspaceMultiUser.Acquire(settingsFolder, 600000);
        }

        private bool BeginOperation(string text)
        {
            if (busy) return false;
            busy = true;
            runAllButton.Enabled = false;
            runAllMenuButton.Enabled = false;
            progress.Visible = true;
            progress.Style = ProgressBarStyle.Marquee;
            SetStatus(text, Color.FromArgb(30, 64, 175), Color.FromArgb(219, 234, 254));
            return true;
        }

        private bool BeginUserOperation(string status, string action)
        {
            if (!BeginOperation(status)) return false;
            StartUserLogContext(action);
            return true;
        }

        private void StartUserLogContext(string action)
        {
            activeLogSession = GetSessionUserName();
            Log(action);
        }

        private void EndUserLogContext()
        {
            activeLogSession = null;
        }

        private void EndOperation()
        {
            if (InvokeRequired) { Invoke((Action)EndOperation); return; }
            busy = false;
            runAllButton.Enabled = true;
            runAllMenuButton.Enabled = true;
            progress.Visible = false;
            progress.Style = ProgressBarStyle.Continuous;
            RefreshLibraryWhenStable();
        }

        private void RenameDocuments()
        {
            Log(UiText.Get("Analyse des noms de documents…", "Analyzing document names…"));
            string[] files = DocumentSupport.GetDocumentFiles(wordFolder);
            ProcedureTag.EnsureUniqueCodes(files.Select(Path.GetFileNameWithoutExtension));
            string activePrefix = ProcedureTag.NormalizePrefixOrDefault(tagPrefix);
            Log(UiText.Get("Préfixe actif : ", "Active prefix: ") + ProcedureTag.FormatCode(activePrefix, 1));

            var used = new HashSet<int>();
            foreach (string path in files)
            {
                int number;
                if (ProcedureTag.TryGetNumber(Path.GetFileNameWithoutExtension(path), activePrefix, out number)) used.Add(number);
            }

            int next = 1;
            int renamed = 0;
            var renamedProcedures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in files)
            {
                if (ProcedureTag.HasCode(Path.GetFileNameWithoutExtension(path))) continue;
                while (next <= ProcedureTag.MaximumNumber && used.Contains(next)) next++;
                if (next > ProcedureTag.MaximumNumber)
                    throw new InvalidOperationException(UiText.Get(
                        "Tous les numéros disponibles pour le préfixe « " + activePrefix + " » sont déjà utilisés.",
                        "All available numbers for the ‘" + activePrefix + "’ prefix are already in use."));
                string previousProcedureName = Path.GetFileNameWithoutExtension(path);
                string newName = ProcedureTag.FormatCode(activePrefix, next) + " " + Path.GetFileName(path);
                string destination = Path.Combine(wordFolder, newName);
                if (File.Exists(destination)) throw new IOException(UiText.Get("Le fichier existe déjà : ", "The file already exists: ") + newName);
                File.Move(path, destination);
                renamedProcedures[previousProcedureName] = Path.GetFileNameWithoutExtension(destination);
                used.Add(next);
                Log(UiText.Get("Renommé : ", "Renamed: ") + Path.GetFileName(path) + " → " + newName);
                renamed++;
                next++;
            }
            int updatedReferences = ProcedureListStorage.UpdateProcedureNames(
                Path.Combine(settingsFolder, ProcedureListStorage.FileName), renamedProcedures);
            if (updatedReferences > 0)
                Log(UiText.Count(updatedReferences,
                    "{0} référence mise à jour dans les listes de procédures.",
                    "{0} références mises à jour dans les listes de procédures.",
                    "{0} reference updated in the procedure lists.",
                    "{0} references updated in the procedure lists."));
            Log(renamed == 0
                ? UiText.Get("Tous les documents sont déjà numérotés.", "All documents are already numbered.")
                : UiText.Count(renamed, "{0} document renommé.", "{0} documents renommés.", "{0} document renamed.", "{0} documents renamed."));
        }

        private void ConvertDocuments()
        {
            Directory.CreateDirectory(pdfFolder);

            string[] files = DocumentSupport.GetDocumentFiles(wordFolder);
            string duplicateTarget = files.GroupBy(path => Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .FirstOrDefault();
            if (duplicateTarget != null)
                throw new InvalidOperationException(UiText.Get(
                    "Plusieurs documents produiraient le même PDF (« " + duplicateTarget + " »). Renommez d’abord les documents pour leur attribuer un numéro unique.",
                    "Several documents would produce the same PDF (‘" + duplicateTarget + "’). Rename the documents first so each has a unique number."));

            var sourceNames = new HashSet<string>(
                files.Select(Path.GetFileNameWithoutExtension),
                StringComparer.OrdinalIgnoreCase);
            int deleted = 0;
            foreach (string pdf in Directory.GetFiles(pdfFolder, "*.pdf"))
            {
                if (sourceNames.Contains(Path.GetFileNameWithoutExtension(pdf))) continue;
                File.Delete(pdf);
                deleted++;
                Log(UiText.Get("PDF supprimé (document source absent) : ", "PDF deleted (source document missing): ") + Path.GetFileName(pdf));
            }

            string[] filesToConvert = files
                .Where(input => !DocumentSupport.IsPdfCurrent(
                    input,
                    Path.Combine(pdfFolder, Path.GetFileNameWithoutExtension(input) + ".pdf")))
                .ToArray();

            if (deleted > 0) Log(UiText.Count(deleted,
                "{0} PDF orphelin supprimé.", "{0} PDF orphelins supprimés.",
                "{0} orphaned PDF deleted.", "{0} orphaned PDFs deleted."));
            Log(UiText.Count(filesToConvert.Length,
                "{0} document à convertir ou actualiser.", "{0} documents à convertir ou actualiser.",
                "{0} document to convert or update.", "{0} documents to convert or update."));
            if (filesToConvert.Length == 0)
            {
                Log(UiText.Get("Tous les PDF sont à jour.", "All PDFs are up to date."));
                return;
            }

            Type wordType = Type.GetTypeFromProgID("Word.Application");
            string sofficePath = DocumentSupport.FindSofficeConverter();
            string localPdfRenderer = DocumentSupport.FindLocalPdfRenderer();
            if (localPdfRenderer != null) Log(UiText.Get(
                "Moteur principal : conversion locale intégrée (DOCX vers PDF, hors ligne).",
                "Primary engine: built-in local conversion (DOCX to PDF, offline)."));
            else if (wordType != null) Log(UiText.Get("Moteur principal : Microsoft Word.", "Primary engine: Microsoft Word."));
            else if (sofficePath != null) Log(UiText.Get("Moteur de conversion : ", "Conversion engine: ") + DocumentSupport.GetOfficeSuiteName(sofficePath) + ".");
            else Log(UiText.Get(
                "Aucun moteur externe disponible ; seuls les formats OOXML nécessitent le moteur PDF local Windows.",
                "No external engine is available; OOXML formats require the local Windows PDF engine."));

            int success = 0;
            var failures = new List<string>();
            foreach (string input in filesToConvert)
            {
                string finalPdf = Path.Combine(pdfFolder, Path.GetFileNameWithoutExtension(input) + ".pdf");
                string tempFolder = Path.Combine(pdfFolder, ".procedurepilot-convert-" + Guid.NewGuid().ToString("N"));
                string tempPdf = Path.Combine(tempFolder, Path.GetFileNameWithoutExtension(input) + ".pdf");
                try
                {
                    Directory.CreateDirectory(tempFolder);
                    Exception localError = null;
                    if (localPdfRenderer != null && LocalDocxPdfConverter.IsSupported(input))
                    {
                        try { LocalDocxPdfConverter.Convert(input, tempPdf, localPdfRenderer); }
                        catch (Exception ex)
                        {
                            localError = ex;
                            if (wordType != null || sofficePath != null) Log(UiText.Get(
                                "Le moteur local n’a pas pu convertir ", "The local engine could not convert ")
                                + Path.GetFileName(input) + UiText.Get(" ; essai avec un moteur bureautique…", "; trying an office engine…"));
                        }
                    }

                    Exception wordError = null;
                    if (!File.Exists(tempPdf) && wordType != null)
                    {
                        try { ConvertWithMicrosoftWord(wordType, input, tempPdf); }
                        catch (Exception ex)
                        {
                            wordError = ex;
                            if (sofficePath != null) Log(UiText.Get("Word n’a pas pu convertir ", "Word could not convert ")
                                + Path.GetFileName(input) + UiText.Get(" ; essai avec ", "; trying ") + DocumentSupport.GetOfficeSuiteName(sofficePath) + "…");
                        }
                    }

                    if (!File.Exists(tempPdf) && sofficePath != null)
                        ConvertWithSoffice(sofficePath, input, tempFolder);

                    if (!File.Exists(tempPdf) && wordError != null) throw wordError;
                    if (!File.Exists(tempPdf) && localError != null) throw localError;
                    if (!File.Exists(tempPdf) && localPdfRenderer == null && LocalDocxPdfConverter.IsSupported(input))
                        throw new InvalidOperationException(UiText.Get(
                            "Le moteur PDF local Windows (Microsoft Edge ou un navigateur Chromium compatible) est introuvable.",
                            "The local Windows PDF engine (Microsoft Edge or a compatible Chromium browser) could not be found."));
                    if (!File.Exists(tempPdf))
                        throw new InvalidOperationException(UiText.Get(
                            "Ce format ancien nécessite LibreOffice, Apache OpenOffice ou Microsoft Word. Enregistrez-le en .docx pour utiliser le moteur local intégré.",
                            "This legacy format requires LibreOffice, Apache OpenOffice, or Microsoft Word. Save it as .docx to use the built-in local engine."));

                    if (!File.Exists(tempPdf) || new FileInfo(tempPdf).Length == 0) throw new IOException(UiText.Get("Le PDF généré est vide.", "The generated PDF is empty."));
                    if (File.Exists(finalPdf)) File.Replace(tempPdf, finalPdf, null, true);
                    else File.Move(tempPdf, finalPdf);
                    success++;
                    Log(UiText.Get("PDF créé ou actualisé : ", "PDF created or updated: ") + Path.GetFileName(finalPdf));
                }
                catch (Exception ex)
                {
                    failures.Add(Path.GetFileName(input) + " : " + GetBaseExceptionMessage(ex));
                    Log(UiText.Get("ÉCHEC : ", "FAILED: ") + failures[failures.Count - 1]);
                }
                finally
                {
                    DocumentSupport.DeleteConversionFolder(tempFolder, pdfFolder);
                }
            }
            Log(UiText.Format(
                "{0} PDF créé(s) ou actualisé(s), {1} erreur(s).",
                "{0} PDF(s) created or updated, {1} error(s).", success, failures.Count));
            if (failures.Count > 0) throw new InvalidOperationException(UiText.Get(
                "Certaines conversions ont échoué :\r\n\r\n",
                "Some conversions failed:\r\n\r\n") + string.Join("\r\n", failures.ToArray()));
        }

        private static void ConvertWithMicrosoftWord(Type wordType, string input, string outputPdf)
        {
            object word = null;
            object document = null;
            try
            {
                word = Activator.CreateInstance(wordType);
                dynamic app = word;
                app.Visible = false;
                app.DisplayAlerts = 0;
                try { app.AutomationSecurity = 3; } catch { }
                document = app.Documents.Open(input, false, true, false);
                dynamic doc = document;
                doc.ExportAsFixedFormat(outputPdf, 17);
            }
            finally
            {
                try { if (document != null) ((dynamic)document).Close(false); } catch { }
                try { if (word != null) ((dynamic)word).Quit(); } catch { }
                ReleaseCom(document);
                ReleaseCom(word);
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }

        private static void ConvertWithSoffice(string executable, string input, string outputFolder)
        {
            string profileFolder = Path.Combine(outputFolder, "profile");
            Directory.CreateDirectory(profileFolder);
            string profileUri = new Uri(profileFolder + Path.DirectorySeparatorChar).AbsoluteUri;
            string arguments = "--headless --nologo --nodefault --nolockcheck --norestore "
                + QuoteArgument("-env:UserInstallation=" + profileUri) + " --convert-to pdf:writer_pdf_Export --outdir "
                + QuoteArgument(outputFolder) + " " + QuoteArgument(input);

            var start = new ProcessStartInfo(executable, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = outputFolder
            };
            using (Process process = Process.Start(start))
            {
                Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
                Task<string> stderrTask = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(120000))
                {
                    try { process.Kill(); } catch { }
                    throw new TimeoutException(UiText.Get("La conversion a dépassé deux minutes.", "The conversion exceeded two minutes."));
                }
                string stdout = stdoutTask.Result;
                string stderr = stderrTask.Result;
                if (process.ExitCode != 0)
                    throw new InvalidOperationException(UiText.Get("La suite bureautique a retourné le code ", "The office suite returned code ")
                        + process.ExitCode + ". " + (stderr + " " + stdout).Trim());
            }
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private int UpdateXmlIndex()
        {
            if (string.IsNullOrWhiteSpace(indexPath)) indexPath = Path.Combine(settingsFolder, "Index.xml");
            int count = ProcedureXmlIndex.Write(indexPath, rootPath, wordFolder, pdfFolder);
            Log(UiText.Format("Index XML mis à jour avec {0} procédure(s) : ", "XML index updated with {0} procedure(s): ", count) + indexPath);
            return count;
        }

        private int UpdateArchiveIndex()
        {
            if (string.IsNullOrWhiteSpace(archiveIndexPath)) archiveIndexPath = Path.Combine(settingsFolder, "Archive.xml");
            int count = ProcedureArchiveIndex.Write(archiveIndexPath, archiveFolder);
            Log(UiText.Format("Archive XML mise à jour avec {0} document(s) modifiable(s) : ", "Archive XML updated with {0} editable document(s): ", count) + archiveIndexPath);
            return count;
        }

        private async Task AddDocumentAsync()
        {
            if (busy) return;
            using (var dialog = new OpenFileDialog())
            {
                dialog.Title = UiText.Get("Ajouter une procédure", "Add a procedure");
                dialog.Filter = UiText.Get(
                    "Documents texte compatibles|*.doc;*.docx;*.docm;*.dot;*.dotx;*.dotm;*.odt;*.ott;*.fodt;*.rtf;*.sxw;*.stw|Microsoft Word|*.doc;*.docx;*.docm;*.dot;*.dotx;*.dotm|LibreOffice / OpenOffice|*.odt;*.ott;*.fodt;*.sxw;*.stw;*.rtf|Tous les fichiers|*.*",
                    "Compatible text documents|*.doc;*.docx;*.docm;*.dot;*.dotx;*.dotm;*.odt;*.ott;*.fodt;*.rtf;*.sxw;*.stw|Microsoft Word|*.doc;*.docx;*.docm;*.dot;*.dotx;*.dotm|LibreOffice / OpenOffice|*.odt;*.ott;*.fodt;*.sxw;*.stw;*.rtf|All files|*.*");
                dialog.Multiselect = true;
                if (Directory.Exists(wordFolder)) dialog.InitialDirectory = wordFolder;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                int copied = 0;
                var copyPlans = new List<KeyValuePair<string, bool>>();
                foreach (string source in dialog.FileNames)
                {
                    if (!DocumentSupport.IsSupported(source))
                    {
                        LogUser(UiText.Get("Ignoré (format non pris en charge) : ", "Skipped (unsupported format): ") + Path.GetFileName(source));
                        continue;
                    }
                    string destination = Path.Combine(wordFolder, Path.GetFileName(source));
                    if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
                    {
                        LogUser(UiText.Get("Déjà présent dans les procédures modifiables : ", "Already present in editable procedures: ") + Path.GetFileName(source));
                        copied++;
                        continue;
                    }
                    bool replaceExisting = false;
                    if (File.Exists(destination))
                    {
                        DialogResult replace = MessageBox.Show(
                            UiText.Get("Le fichier existe déjà :\r\n", "The file already exists:\r\n") + Path.GetFileName(source)
                                + UiText.Get("\r\n\r\nLe remplacer ?", "\r\n\r\nReplace it?"),
                            UiText.Get("Fichier existant", "Existing file"), MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                        if (replace != DialogResult.Yes) continue;
                        replaceExisting = true;
                    }
                    copyPlans.Add(new KeyValuePair<string, bool>(source, replaceExisting));
                }

                if (copyPlans.Count > 0)
                {
                    WorkspaceWriteLease copyLease = WorkspaceMultiUser.TryAcquire(settingsFolder, 3000);
                    if (copyLease == null)
                    {
                        MessageBox.Show(
                            UiText.Get(
                                "Un autre poste modifie actuellement l’espace partagé. Réessayez dans quelques instants.",
                                "Another computer is currently modifying the shared workspace. Try again in a moment."),
                            UiText.Get("Espace partagé occupé", "Shared workspace busy"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                    using (copyLease)
                    {
                        foreach (KeyValuePair<string, bool> plan in copyPlans)
                        {
                            string source = plan.Key;
                            string destination = Path.Combine(wordFolder, Path.GetFileName(source));
                            if (File.Exists(destination) && !plan.Value)
                            {
                                LogUser(UiText.Get(
                                    "Non ajouté car un autre poste vient de créer ce fichier : ",
                                    "Not added because another computer just created this file: ") + Path.GetFileName(source));
                                continue;
                            }
                            File.Copy(source, destination, plan.Value);
                            LogUser(UiText.Get("Ajouté : ", "Added: ") + Path.GetFileName(source));
                            copied++;
                        }
                        if (copied > 0) WorkspaceMultiUser.PublishChange(settingsFolder, "documents-added");
                    }
                }
                if (copied > 0)
                {
                    LogUser(UiText.Count(copied,
                        "{0} document ajouté. Lancement de la mise à jour complète…",
                        "{0} documents ajoutés. Lancement de la mise à jour complète…",
                        "{0} document added. Starting the full update…",
                        "{0} documents added. Starting the full update…"));
                    await RunFullUpdateAsync();
                }
                else RefreshLibraryWhenStable();
            }
        }

        private void OpenProcedureLists()
        {
            if (busy) return;
            try
            {
                string path = Path.Combine(settingsFolder, ProcedureListStorage.FileName);
                using (var dialog = new ProcedureListsForm(path, allItems.Select(item => item.Name), OpenProcedureByName, LogUser))
                    dialog.ShowDialog(this);
            }
            catch (Exception ex)
            {
                string message = GetBaseExceptionMessage(ex);
                LogUser(UiText.Get("ERREUR listes de procédures : ", "PROCEDURE LIST ERROR: ") + message);
                MessageBox.Show(message, UiText.Get("Impossible d’ouvrir les listes", "Unable to open lists"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OpenProcedureByName(string name)
        {
            ProcedureItem item = allItems.FirstOrDefault(value => string.Equals(value.Name, name, StringComparison.OrdinalIgnoreCase));
            if (item == null)
            {
                MessageBox.Show(
                    UiText.Get("Cette procédure n’est plus disponible dans la bibliothèque.", "This procedure is no longer available in the library."),
                    UiText.Get("Procédure absente", "Missing procedure"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            OpenPath(item.PdfPath ?? item.WordPath);
        }

        private async Task ArchiveSelectedAsync()
        {
            if (busy || procedureList.SelectedItems.Count == 0) return;
            ProcedureItem item = procedureList.SelectedItems[0].Tag as ProcedureItem;
            if (item == null) return;
            if (MessageBox.Show(
                UiText.Get(
                    "Archiver les fichiers de « " + item.Name + " » ?\r\n\r\nIls seront déplacés dans Archive et resteront récupérables.",
                    "Archive the files for ‘" + item.Name + "’?\r\n\r\nThey will be moved to Archive and remain recoverable."),
                UiText.Get("Confirmer l’archivage", "Confirm archiving"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            if (!BeginUserOperation(UiText.Get("ARCHIVAGE", "ARCHIVING"), UiText.Get("Archivage demandé pour : ", "Archiving requested for: ") + item.Name)) return;
            try
            {
                await Task.Run(delegate
                {
                    ExecuteSharedWrite(delegate
                    {
                        string dated = Path.Combine(archiveFolder, DateTime.Now.ToString("yyyy-MM-dd"));
                        Directory.CreateDirectory(dated);
                        MoveToArchive(item.WordPath, dated);
                        MoveToArchive(item.PdfPath, dated);
                        UpdateXmlIndex();
                        UpdateArchiveIndex();
                    }, "archive-procedure");
                });
                Log(UiText.Get("Archivé : ", "Archived: ") + item.Name);
            }
            catch (Exception ex) { ShowOperationError(ex); }
            finally
            {
                lastDocumentState = CaptureDocumentState();
                lastArchiveState = CaptureArchiveState();
                EndUserLogContext();
                EndOperation();
            }
        }

        private static void MoveToArchive(string source, string folder)
        {
            if (string.IsNullOrWhiteSpace(source) || !File.Exists(source)) return;
            string fileName = Path.GetFileName(source);
            string baseName = Path.GetFileNameWithoutExtension(source);
            string extension = Path.GetExtension(source);
            string stamp = DateTime.Now.ToString("HHmmss");
            for (int attempt = 0; attempt <= 10000; attempt++)
            {
                string candidateName = attempt == 0
                    ? fileName
                    : baseName + "_" + stamp + (attempt == 1 ? string.Empty : "_" + attempt) + extension;
                string destination = Path.Combine(folder, candidateName);
                if (File.Exists(destination)) continue;
                try
                {
                    File.Move(source, destination);
                    return;
                }
                catch (IOException)
                {
                    if (File.Exists(destination)) continue;
                    throw;
                }
            }

            throw new IOException(UiText.Get(
                "Impossible de trouver un nom de fichier unique dans l’archive pour : " + fileName,
                "Unable to find a unique archive file name for: " + fileName));
        }

        private void OpenSelectedPdfOrWord()
        {
            if (procedureList.SelectedItems.Count == 0) return;
            ProcedureItem item = procedureList.SelectedItems[0].Tag as ProcedureItem;
            if (item == null) return;
            OpenPath(item.PdfPath ?? item.WordPath);
        }

        private void OpenSelected(bool pdf)
        {
            if (procedureList.SelectedItems.Count == 0) return;
            ProcedureItem item = procedureList.SelectedItems[0].Tag as ProcedureItem;
            if (item == null) return;
            string path = pdf ? item.PdfPath : item.WordPath;
            if (path == null) MessageBox.Show(
                pdf ? UiText.Get("Aucun PDF n’est disponible.", "No PDF is available.") : UiText.Get("Aucun document source n’est disponible.", "No source document is available."),
                UiText.Get("Fichier absent", "Missing file"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            else OpenPath(path);
        }

        private void OpenPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || (!File.Exists(path) && !Directory.Exists(path))) return;
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                LogUser(UiText.Get("Ouverture : ", "Opened: ") + path);
            }
            catch (Exception ex)
            {
                string message = GetBaseExceptionMessage(ex);
                LogUser(UiText.Get("ÉCHEC ouverture : ", "FAILED to open: ") + path + " — " + message);
                MessageBox.Show(message, UiText.Get("Ouverture impossible", "Unable to open"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ShowOperationError(Exception ex)
        {
            string message = GetBaseExceptionMessage(ex);
            Log(UiText.Get("ERREUR : ", "ERROR: ") + message);
            if (InvokeRequired) { Invoke((Action)(delegate { MessageBox.Show(message, UiText.Get("Opération interrompue", "Operation interrupted"), MessageBoxButtons.OK, MessageBoxIcon.Error); })); }
            else MessageBox.Show(message, UiText.Get("Opération interrompue", "Operation interrupted"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private void SetStatus(string text, Color background, Color foreground)
        {
            if (InvokeRequired) { Invoke((Action)(delegate { SetStatus(text, background, foreground); })); return; }
            statusLabel.Text = text;
            statusLabel.BackColor = background;
            statusLabel.ForeColor = foreground;
        }

        private void Log(string message)
        {
            Log(message, activeLogSession);
        }

        private void LogUser(string message)
        {
            Log(message, GetSessionUserName());
        }

        private void LogSystem(string message)
        {
            Log(message, null);
        }

        private void Log(string message, string sessionUser)
        {
            if (InvokeRequired)
            {
                Invoke((Action)(delegate { Log(message, sessionUser); }));
                return;
            }

            DateTimeOffset now = DateTimeOffset.Now;
            bool isUserAction = !string.IsNullOrWhiteSpace(sessionUser);
            string source = isUserAction ? "USER session=" + sessionUser : "SYSTEM";
            string normalizedMessage = NormalizeLogMessage(message);
            string fileLine = now.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz") + " [" + source + "] " + normalizedMessage;
            string screenSource = isUserAction
                ? UiText.Get("[UTILISATEUR : ", "[USER: ") + sessionUser + "]"
                : UiText.Get("[SYSTÈME]", "[SYSTEM]");
            logBox.AppendText(now.ToString("HH:mm:ss") + " " + screenSource + "  " + normalizedMessage + Environment.NewLine);
            AppendLogFile(fileLine);
        }

        private static string NormalizeLogMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return UiText.Get("(message vide)", "(empty message)");
            return Regex.Replace(message.Trim(), "[\\r\\n]+", " | ");
        }

        private void AppendLogFile(string line)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(settingsFolder)) return;
                Directory.CreateDirectory(settingsFolder);
                string path = Path.Combine(settingsFolder, "Logs.txt");
                lock (FileLogLock)
                {
                    IOException lastWriteError = null;
                    FileStream sharedLogLease = null;
                    string sharedLogLockPath = Path.Combine(settingsFolder, ".procedurepilot-log.lock");
                    for (int attempt = 0; attempt < 40; attempt++)
                    {
                        try
                        {
                            sharedLogLease = new FileStream(sharedLogLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                            break;
                        }
                        catch (IOException ex)
                        {
                            lastWriteError = ex;
                            if (attempt < 39) Thread.Sleep(Math.Min(100, 10 * (attempt + 1)));
                        }
                    }
                    if (sharedLogLease == null)
                        throw lastWriteError ?? new IOException(UiText.Get("Le journal est temporairement verrouillé.", "The log is temporarily locked."));
                    using (sharedLogLease)
                    using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                        writer.WriteLine(line);
                }
            }
            catch (Exception ex)
            {
                if (logFileErrorDisplayed) return;
                logFileErrorDisplayed = true;
                logBox.AppendText(DateTime.Now.ToString("HH:mm:ss") + " " + UiText.Get("[SYSTÈME]", "[SYSTEM]") + "  "
                    + UiText.Get("Impossible d’écrire Logs.txt : ", "Unable to write Logs.txt: ")
                    + GetBaseExceptionMessage(ex) + Environment.NewLine);
            }
        }

        private static string GetSessionUserName()
        {
            string user = Environment.UserName;
            string domain = Environment.UserDomainName;
            if (string.IsNullOrWhiteSpace(user)) return UiText.Get("inconnue", "unknown");
            return string.IsNullOrWhiteSpace(domain) ? user : domain + "\\" + user;
        }

        private static void ReleaseCom(object value)
        {
            if (value == null) return;
            try { if (Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); } catch { }
        }

        private static string GetBaseExceptionMessage(Exception ex)
        {
            while (ex.InnerException != null) ex = ex.InnerException;
            return ex.Message;
        }

        private static string FormatSize(long bytes)
        {
            if (bytes >= 1048576) return (bytes / 1048576d).ToString("0.0", UiText.Culture) + UiText.Get(" Mo", " MB");
            if (bytes >= 1024) return (bytes / 1024d).ToString("0", UiText.Culture) + UiText.Get(" Ko", " KB");
            return bytes + UiText.Get(" o", " B");
        }

        private static Icon CreateAppIcon()
        {
            using (var bitmap = new Bitmap(32, 32))
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.FromArgb(37, 99, 235));
                using (var brush = new SolidBrush(Color.White))
                using (var font = new Font("Segoe UI", 19F, FontStyle.Bold, GraphicsUnit.Pixel))
                    g.DrawString("P", font, brush, 7, 4);
                return Icon.FromHandle(bitmap.GetHicon());
            }
        }

        private sealed class ProcedureItem
        {
            public string Name;
            public string WordPath;
            public string PdfPath;
        }
    }

    internal sealed class WorkspaceSettings
    {
        public string RootPath;
        public string DocumentsPath;
        public string PdfPath;
        public string ArchivePath;
        public string IndexPath;
        public string ArchiveIndexPath;
        public string SettingsFolderPath;
        public string Language;
        public string TagPrefix;

        internal static WorkspaceSettings CreateDefault(string root)
        {
            string fullRoot = Path.GetFullPath(root);
            string dataFolder = Path.Combine(fullRoot, "Data");
            return new WorkspaceSettings
            {
                RootPath = fullRoot,
                DocumentsPath = Path.Combine(fullRoot, "Documents"),
                PdfPath = Path.Combine(fullRoot, "PDFs"),
                ArchivePath = Path.Combine(fullRoot, "Archive"),
                IndexPath = Path.Combine(dataFolder, "Index.xml"),
                ArchiveIndexPath = Path.Combine(dataFolder, "Archive.xml"),
                SettingsFolderPath = dataFolder,
                Language = UiText.French,
                TagPrefix = ProcedureTag.DefaultPrefix
            };
        }

        internal static WorkspaceSettings Load(string settingsPath, string defaultRoot)
        {
            WorkspaceSettings defaults = CreateDefault(defaultRoot);
            if (!File.Exists(settingsPath)) return defaults;
            Exception lastError = null;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    XDocument document = XDocument.Load(settingsPath);
                    XElement root = document.Root;
                    if (root == null || (root.Name.LocalName != "ApplicationSettings" && root.Name.LocalName != "ProcedurePilotSettings"))
                        throw new InvalidDataException("Invalid settings root element.");
                    string configuredRoot = Read(root, "RootPath") ?? defaults.RootPath;
                    WorkspaceSettings configuredDefaults = CreateDefault(configuredRoot);
                    string configuredDataFolder = Read(root, "DataPath") ?? Read(root, "SettingsFolderPath") ?? configuredDefaults.SettingsFolderPath;
                    configuredDataFolder = Path.GetFullPath(configuredDataFolder);
                    return new WorkspaceSettings
                    {
                        RootPath = Path.GetFullPath(configuredRoot),
                        DocumentsPath = Path.GetFullPath(Read(root, "DocumentsPath") ?? configuredDefaults.DocumentsPath),
                        PdfPath = Path.GetFullPath(Read(root, "PdfPath") ?? configuredDefaults.PdfPath),
                        ArchivePath = Path.GetFullPath(Read(root, "ArchivePath") ?? configuredDefaults.ArchivePath),
                        IndexPath = Path.Combine(configuredDataFolder, "Index.xml"),
                        ArchiveIndexPath = Path.Combine(configuredDataFolder, "Archive.xml"),
                        SettingsFolderPath = configuredDataFolder,
                        Language = UiText.NormalizeLanguage(Read(root, "Language") ?? configuredDefaults.Language),
                        TagPrefix = ProcedureTag.NormalizePrefixOrDefault(Read(root, "TagPrefix") ?? configuredDefaults.TagPrefix)
                    };
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    if (attempt < 4) Thread.Sleep(50 * (attempt + 1));
                }
            }

            throw new InvalidDataException(UiText.Get(
                "Le fichier de paramètres est illisible et n’a pas été remplacé :\r\n" + Path.GetFullPath(settingsPath)
                    + "\r\n\r\nRestaurez ou corrigez ce fichier avant de relancer Procedure Pilot.",
                "The settings file cannot be read and was not replaced:\r\n" + Path.GetFullPath(settingsPath)
                    + "\r\n\r\nRestore or correct this file before restarting Procedure Pilot."), lastError);
        }

        internal void Save(string settingsPath)
        {
            string folder = Path.GetDirectoryName(settingsPath);
            if (!string.IsNullOrWhiteSpace(folder)) Directory.CreateDirectory(folder);
            var document = new XDocument(
                new XElement("ApplicationSettings",
                    new XAttribute("version", "5"),
                    new XElement("RootPath", RootPath),
                    new XElement("DocumentsPath", DocumentsPath),
                    new XElement("PdfPath", PdfPath),
                    new XElement("ArchivePath", ArchivePath),
                    new XElement("DataPath", SettingsFolderPath),
                    new XElement("Language", UiText.NormalizeLanguage(Language)),
                    new XElement("TagPrefix", ProcedureTag.NormalizePrefixOrDefault(TagPrefix)),
                    new XElement("WatchFolders", true),
                    new XElement("DebounceMilliseconds", 1500)));

            string tempPath = settingsPath + ".tmp-" + Guid.NewGuid().ToString("N");
            document.Save(tempPath);
            try
            {
                if (File.Exists(settingsPath))
                {
                    try { File.Replace(tempPath, settingsPath, null, true); }
                    catch (IOException)
                    {
                        File.Copy(tempPath, settingsPath, true);
                        File.Delete(tempPath);
                    }
                }
                else File.Move(tempPath, settingsPath);
            }
            finally
            {
                if (File.Exists(tempPath)) try { File.Delete(tempPath); } catch { }
            }
        }

        private static string Read(XElement root, string name)
        {
            XElement value = root.Element(name);
            if (value == null || string.IsNullOrWhiteSpace(value.Value)) return null;
            return value.Value.Trim();
        }
    }

    internal static class SettingsStorage
    {
        private const string SettingsFileName = "Settings.xml";
        private const string LegacySettingsFileName = "ProcedurePilot.settings.xml";
        private const string LocationFileName = "ProcedurePilot.data-folder";
        private const string LegacyLocationFileName = "ProcedurePilot.settings-folder";

        internal static string ResolveSettingsPath(string defaultRoot)
        {
            string executableFolder = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            string[] locationPaths =
            {
                Path.Combine(executableFolder, "Data", LocationFileName),
                Path.Combine(executableFolder, "Settings", LegacyLocationFileName)
            };
            foreach (string locationPath in locationPaths)
            {
                try
                {
                    if (!File.Exists(locationPath)) continue;
                    string configuredFolder = Path.GetFullPath(File.ReadAllText(locationPath, Encoding.UTF8).Trim());
                    string current = Path.Combine(configuredFolder, SettingsFileName);
                    if (File.Exists(current)) return current;
                    string legacy = Path.Combine(configuredFolder, LegacySettingsFileName);
                    if (File.Exists(legacy)) return legacy;
                }
                catch { }
            }

            string defaultPath = Path.Combine(WorkspaceSettings.CreateDefault(defaultRoot).SettingsFolderPath, SettingsFileName);
            if (File.Exists(defaultPath)) return defaultPath;
            string[] legacyPaths =
            {
                Path.Combine(defaultRoot, "Settings", LegacySettingsFileName),
                Path.Combine(executableFolder, LegacySettingsFileName)
            };
            return legacyPaths.FirstOrDefault(File.Exists) ?? defaultPath;
        }

        internal static void MigrateLegacyLayout(WorkspaceSettings settings)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            string root = Path.GetFullPath(settings.RootPath);
            settings.DocumentsPath = MigrateDefaultDirectory(settings.DocumentsPath, root, "Procedures_Modifiables", "Documents");
            settings.PdfPath = MigrateDefaultDirectory(settings.PdfPath, root, "Procedures_PDF", "PDFs");
            settings.ArchivePath = MigrateDefaultDirectory(settings.ArchivePath, root, "_ARCHIVE", "Archive");
            settings.SettingsFolderPath = MigrateDefaultDirectory(settings.SettingsFolderPath, root, "Settings", "Data");
            Normalize(settings);
        }

        internal static string Initialize(WorkspaceSettings settings)
        {
            Normalize(settings);
            string targetPath = Path.Combine(settings.SettingsFolderPath, SettingsFileName);
            settings.Save(targetPath);
            WriteLocation(settings.SettingsFolderPath);
            return targetPath;
        }

        internal static string Store(WorkspaceSettings settings)
        {
            Normalize(settings);
            string targetPath = Path.Combine(settings.SettingsFolderPath, SettingsFileName);
            settings.Save(targetPath);
            WriteLocation(settings.SettingsFolderPath);
            return targetPath;
        }

        private static void Normalize(WorkspaceSettings settings)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            string folder = string.IsNullOrWhiteSpace(settings.SettingsFolderPath)
                ? Path.Combine(settings.RootPath, "Data")
                : settings.SettingsFolderPath;
            settings.SettingsFolderPath = Path.GetFullPath(folder);
            settings.RootPath = Path.GetFullPath(settings.RootPath);
            settings.DocumentsPath = Path.GetFullPath(settings.DocumentsPath);
            settings.PdfPath = Path.GetFullPath(settings.PdfPath);
            settings.ArchivePath = Path.GetFullPath(settings.ArchivePath);
            settings.Language = UiText.NormalizeLanguage(settings.Language);
            settings.TagPrefix = ProcedureTag.NormalizePrefixOrDefault(settings.TagPrefix);
            settings.IndexPath = Path.Combine(settings.SettingsFolderPath, "Index.xml");
            settings.ArchiveIndexPath = Path.Combine(settings.SettingsFolderPath, "Archive.xml");
        }

        private static string MigrateDefaultDirectory(string configuredPath, string root, string legacyName, string currentName)
        {
            string legacyPath = Path.Combine(root, legacyName);
            string currentPath = Path.Combine(root, currentName);
            string configured = Path.GetFullPath(configuredPath);
            bool isLegacyDefault = string.Equals(configured, Path.GetFullPath(legacyPath), StringComparison.OrdinalIgnoreCase);
            bool isCurrentDefault = string.Equals(configured, Path.GetFullPath(currentPath), StringComparison.OrdinalIgnoreCase);
            if (!isLegacyDefault && !isCurrentDefault) return configured;

            try
            {
                if (Directory.Exists(legacyPath) && !Directory.Exists(currentPath))
                    Directory.Move(legacyPath, currentPath);
                else if (Directory.Exists(legacyPath) && Directory.Exists(currentPath)
                    && Directory.GetFileSystemEntries(currentPath).Length == 0)
                {
                    Directory.Delete(currentPath);
                    Directory.Move(legacyPath, currentPath);
                }
                if (!Directory.Exists(legacyPath)) return Path.GetFullPath(currentPath);
                if (Directory.Exists(currentPath)) return isLegacyDefault ? Path.GetFullPath(legacyPath) : Path.GetFullPath(currentPath);
            }
            catch { }
            return isLegacyDefault ? Path.GetFullPath(legacyPath) : Path.GetFullPath(currentPath);
        }

        internal static void CleanupLegacyXmlFiles(WorkspaceSettings settings)
        {
            if (!HasValidCurrentXmlFiles(settings)) return;
            string executableFolder = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            string[] folders =
            {
                settings.SettingsFolderPath,
                Path.Combine(settings.RootPath, "Settings"),
                settings.RootPath,
                executableFolder
            };
            foreach (string folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                DeleteLegacyXml(Path.Combine(folder, "Index_Procedures.xml"), settings);
                DeleteLegacyXml(Path.Combine(folder, LegacySettingsFileName), settings);
            }
        }

        internal static void CleanupSupersededDataFolder(string previousFolder, WorkspaceSettings settings)
        {
            if (string.IsNullOrWhiteSpace(previousFolder) || settings == null || !HasValidCurrentXmlFiles(settings)) return;
            string oldFolder = Path.GetFullPath(previousFolder);
            if (string.Equals(oldFolder, Path.GetFullPath(settings.SettingsFolderPath), StringComparison.OrdinalIgnoreCase)) return;
            foreach (string name in new[] { "Index.xml", "Settings.xml", "Archive.xml" })
            {
                string path = Path.Combine(oldFolder, name);
                if (File.Exists(path)) File.Delete(path);
            }
        }

        private static bool HasValidCurrentXmlFiles(WorkspaceSettings settings)
        {
            try
            {
                return HasRoot(settings.IndexPath, "DocumentIndex")
                    && HasRoot(Path.Combine(settings.SettingsFolderPath, SettingsFileName), "ApplicationSettings")
                    && HasRoot(settings.ArchiveIndexPath, "ArchiveIndex");
            }
            catch { return false; }
        }

        private static bool HasRoot(string path, string rootName)
        {
            if (!File.Exists(path)) return false;
            XElement root = XDocument.Load(path).Root;
            return root != null && root.Name.LocalName == rootName;
        }

        private static void DeleteLegacyXml(string path, WorkspaceSettings settings)
        {
            string fullPath = Path.GetFullPath(path);
            if (string.Equals(fullPath, Path.GetFullPath(settings.IndexPath), StringComparison.OrdinalIgnoreCase)
                || string.Equals(fullPath, Path.GetFullPath(settings.ArchiveIndexPath), StringComparison.OrdinalIgnoreCase)
                || string.Equals(fullPath, Path.Combine(Path.GetFullPath(settings.SettingsFolderPath), SettingsFileName), StringComparison.OrdinalIgnoreCase)) return;
            if (File.Exists(fullPath)) File.Delete(fullPath);
        }

        private static void WriteLocation(string settingsFolder)
        {
            string executableFolder = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            string locationPath = Path.Combine(executableFolder, "Data", LocationFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(locationPath));
            string normalizedFolder = Path.GetFullPath(settingsFolder);
            try
            {
                if (File.Exists(locationPath)
                    && string.Equals(
                        Path.GetFullPath(File.ReadAllText(locationPath, Encoding.UTF8).Trim()),
                        normalizedFolder,
                        StringComparison.OrdinalIgnoreCase)) return;
            }
            catch { }
            string tempPath = locationPath + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(tempPath, normalizedFolder, Encoding.UTF8);
            try
            {
                if (File.Exists(locationPath))
                {
                    try { File.Replace(tempPath, locationPath, null, true); }
                    catch (IOException)
                    {
                        File.Copy(tempPath, locationPath, true);
                        File.Delete(tempPath);
                    }
                }
                else File.Move(tempPath, locationPath);
            }
            finally
            {
                if (File.Exists(tempPath)) try { File.Delete(tempPath); } catch { }
            }

            string legacyLocation = Path.Combine(executableFolder, "Settings", LegacyLocationFileName);
            if (!string.Equals(legacyLocation, locationPath, StringComparison.OrdinalIgnoreCase) && File.Exists(legacyLocation))
                try { File.Delete(legacyLocation); } catch { }
        }
    }

    internal sealed class SettingsForm : Form
    {
        private readonly string rootPath;
        private readonly TextBox documentsBox;
        private readonly TextBox pdfBox;
        private readonly TextBox archiveBox;
        private readonly TextBox settingsFolderBox;
        private readonly TextBox indexBox;
        private readonly ComboBox languageBox;
        private readonly TextBox tagPrefixBox;
        private readonly Label tagPreviewLabel;

        internal WorkspaceSettings SelectedSettings { get; private set; }

        internal SettingsForm(WorkspaceSettings current)
        {
            SelectedSettings = current;
            rootPath = current.RootPath;
            Text = UiText.Get("Paramètres de Procedure Pilot", "Procedure Pilot settings");
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(840, 530);
            BackColor = Color.FromArgb(248, 250, 252);
            Font = new Font("Segoe UI", 9F);

            var title = new Label
            {
                Text = UiText.Get("EMPLACEMENTS DE L’ESPACE DOCUMENTAIRE", "DOCUMENT WORKSPACE LOCATIONS"),
                Font = new Font("Segoe UI Semibold", 13F, FontStyle.Bold),
                ForeColor = Color.FromArgb(15, 23, 42),
                AutoSize = true,
                Location = new Point(24, 20)
            };
            Controls.Add(title);

            var help = new Label
            {
                Text = UiText.Get(
                    "Choisissez où Procedure Pilot doit lire et enregistrer chaque élément.",
                    "Choose where Procedure Pilot should read and save each item."),
                ForeColor = Color.FromArgb(100, 116, 139),
                AutoSize = true,
                Location = new Point(26, 50)
            };
            Controls.Add(help);

            documentsBox = AddPathRow(UiText.Get("Procédures modifiables", "Editable procedures"), current.DocumentsPath, 84,
                delegate { BrowseFolder(documentsBox, UiText.Get("Choisir le dossier des procédures modifiables", "Choose the editable procedures folder")); });
            pdfBox = AddPathRow(UiText.Get("Procédures PDF", "PDF procedures"), current.PdfPath, 134,
                delegate { BrowseFolder(pdfBox, UiText.Get("Choisir le dossier des PDF", "Choose the PDF folder")); });
            archiveBox = AddPathRow(UiText.Get("Archives", "Archives"), current.ArchivePath, 184,
                delegate { BrowseFolder(archiveBox, UiText.Get("Choisir le dossier des archives", "Choose the archive folder")); });
            settingsFolderBox = AddPathRow(UiText.Get("Données de l’application", "Application data"), current.SettingsFolderPath, 234, BrowseSettingsFolder);
            indexBox = AddPathRow(UiText.Get("Trois fichiers XML", "Three XML files"), XmlFilesDisplay(current.SettingsFolderPath), 284, null);

            var languageLabel = new Label
            {
                Text = UiText.Get("Langue de l’interface", "Interface language"),
                ForeColor = Color.FromArgb(51, 65, 85),
                TextAlign = ContentAlignment.MiddleLeft,
                Location = new Point(26, 337),
                Size = new Size(164, 30)
            };
            Controls.Add(languageLabel);
            languageBox = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("Segoe UI", 9.5F),
                Location = new Point(198, 334),
                Size = new Size(220, 30)
            };
            languageBox.Items.Add("Français");
            languageBox.Items.Add("English");
            languageBox.SelectedIndex = string.Equals(current.Language, UiText.English, StringComparison.Ordinal) ? 1 : 0;
            Controls.Add(languageBox);

            var tagPrefixLabel = new Label
            {
                Text = UiText.Get("Préfixe du tag partagé", "Shared tag prefix"),
                ForeColor = Color.FromArgb(51, 65, 85),
                TextAlign = ContentAlignment.MiddleLeft,
                Location = new Point(26, 379),
                Size = new Size(164, 30)
            };
            Controls.Add(tagPrefixLabel);
            tagPrefixBox = new TextBox
            {
                Text = ProcedureTag.NormalizePrefixOrDefault(current.TagPrefix),
                CharacterCasing = CharacterCasing.Upper,
                MaxLength = ProcedureTag.MaximumPrefixLength,
                Font = new Font("Segoe UI", 9.5F),
                BorderStyle = BorderStyle.FixedSingle,
                Location = new Point(198, 376),
                Size = new Size(220, 30)
            };
            Controls.Add(tagPrefixBox);
            tagPreviewLabel = new Label
            {
                ForeColor = Color.FromArgb(100, 116, 139),
                TextAlign = ContentAlignment.MiddleLeft,
                Location = new Point(430, 376),
                Size = new Size(386, 30)
            };
            Controls.Add(tagPreviewLabel);
            tagPrefixBox.TextChanged += delegate { UpdateTagPreview(); };
            UpdateTagPreview();

            var defaultButton = new Button
            {
                Text = UiText.Get("Sous-dossiers standard", "Standard subfolders"),
                Location = new Point(198, 462),
                Size = new Size(170, 38),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White,
                ForeColor = Color.FromArgb(30, 41, 59)
            };
            defaultButton.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225);
            defaultButton.Click += delegate { SetDefaultsFromRoot(); };
            Controls.Add(defaultButton);

            var cancelButton = new Button
            {
                Text = UiText.Get("Annuler", "Cancel"),
                DialogResult = DialogResult.Cancel,
                Location = new Point(606, 462),
                Size = new Size(92, 38),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White
            };
            cancelButton.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225);
            Controls.Add(cancelButton);

            var saveButton = new Button
            {
                Text = UiText.Get("Enregistrer", "Save"),
                Location = new Point(708, 462),
                Size = new Size(108, 38),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(37, 99, 235),
                ForeColor = Color.White
            };
            saveButton.FlatAppearance.BorderColor = Color.FromArgb(37, 99, 235);
            saveButton.Click += SaveAndClose;
            Controls.Add(saveButton);

            AcceptButton = saveButton;
            CancelButton = cancelButton;
        }

        private TextBox AddPathRow(string labelText, string value, int top, Action browseAction)
        {
            var label = new Label
            {
                Text = labelText,
                ForeColor = Color.FromArgb(51, 65, 85),
                TextAlign = ContentAlignment.MiddleLeft,
                Location = new Point(26, top + 3),
                Size = new Size(164, 30)
            };
            Controls.Add(label);

            var box = new TextBox
            {
                Text = value ?? "",
                Font = new Font("Segoe UI", 9.5F),
                BorderStyle = BorderStyle.FixedSingle,
                Location = new Point(198, top),
                Size = new Size(546, 30)
            };
            Controls.Add(box);

            if (browseAction == null)
            {
                box.Size = new Size(618, 30);
                box.ReadOnly = true;
                box.BackColor = Color.FromArgb(241, 245, 249);
                return box;
            }

            var browse = new Button
            {
                Text = "…",
                Location = new Point(754, top - 1),
                Size = new Size(62, 32),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White,
                Font = new Font("Segoe UI Semibold", 12F, FontStyle.Bold)
            };
            browse.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225);
            browse.Click += delegate { browseAction(); };
            Controls.Add(browse);
            return box;
        }

        private void BrowseFolder(TextBox box, string description)
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = description;
                if (Directory.Exists(box.Text)) dialog.SelectedPath = box.Text;
                else if (Directory.Exists(rootPath)) dialog.SelectedPath = rootPath;
                if (dialog.ShowDialog(this) == DialogResult.OK) box.Text = dialog.SelectedPath;
            }
        }

        private void BrowseSettingsFolder()
        {
            string previousFolder = settingsFolderBox.Text;
            BrowseFolder(settingsFolderBox, UiText.Get("Choisir le dossier des données de l’application", "Choose the application data folder"));
            if (string.Equals(previousFolder, settingsFolderBox.Text, StringComparison.OrdinalIgnoreCase)) return;
            indexBox.Text = XmlFilesDisplay(settingsFolderBox.Text);
        }

        private static string XmlFilesDisplay(string folder)
        {
            return Path.Combine(folder ?? "", "Index.xml") + "  •  "
                + Path.Combine(folder ?? "", "Settings.xml") + "  •  "
                + Path.Combine(folder ?? "", "Archive.xml");
        }

        private void SetDefaultsFromRoot()
        {
            if (string.IsNullOrWhiteSpace(rootPath)) return;
            try
            {
                string root = Path.GetFullPath(rootPath);
                documentsBox.Text = Path.Combine(root, "Documents");
                pdfBox.Text = Path.Combine(root, "PDFs");
                archiveBox.Text = Path.Combine(root, "Archive");
                settingsFolderBox.Text = Path.Combine(root, "Data");
                indexBox.Text = XmlFilesDisplay(settingsFolderBox.Text);
            }
            catch { }
        }

        private void UpdateTagPreview()
        {
            string normalized;
            if (ProcedureTag.TryNormalizePrefix(tagPrefixBox.Text, out normalized))
            {
                tagPreviewLabel.ForeColor = Color.FromArgb(100, 116, 139);
                tagPreviewLabel.Text = UiText.Get("Aperçu : ", "Preview: ")
                    + ProcedureTag.FormatCode(normalized, 1)
                    + UiText.Get(" · nouveaux documents", " · new documents");
            }
            else
            {
                tagPreviewLabel.ForeColor = Color.FromArgb(185, 28, 28);
                tagPreviewLabel.Text = UiText.Get("Préfixe invalide", "Invalid prefix");
            }
        }

        private void SaveAndClose(object sender, EventArgs e)
        {
            try
            {
                if (new[] { documentsBox.Text, pdfBox.Text, archiveBox.Text, settingsFolderBox.Text }.Any(string.IsNullOrWhiteSpace))
                    throw new InvalidOperationException(UiText.Get(
                        "Tous les dossiers, dont celui des données de l’application, doivent être renseignés.",
                        "All folders, including the application data folder, must be specified."));

                string selectedRoot = Path.GetFullPath(rootPath);
                string selectedSettingsFolder = Path.GetFullPath(settingsFolderBox.Text.Trim());
                string selectedTagPrefix = ProcedureTag.ValidateAndNormalizePrefix(tagPrefixBox.Text);

                SelectedSettings = new WorkspaceSettings
                {
                    RootPath = selectedRoot,
                    DocumentsPath = Path.GetFullPath(documentsBox.Text.Trim()),
                    PdfPath = Path.GetFullPath(pdfBox.Text.Trim()),
                    ArchivePath = Path.GetFullPath(archiveBox.Text.Trim()),
                    IndexPath = Path.Combine(selectedSettingsFolder, "Index.xml"),
                    ArchiveIndexPath = Path.Combine(selectedSettingsFolder, "Archive.xml"),
                    SettingsFolderPath = selectedSettingsFolder,
                    Language = languageBox.SelectedIndex == 1 ? UiText.English : UiText.French,
                    TagPrefix = selectedTagPrefix
                };
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, UiText.Get("Paramètres invalides", "Invalid settings"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }

    internal static class DocumentSupport
    {
        internal static readonly string[] Extensions =
        {
            ".docx", ".doc", ".docm", ".dotx", ".dot", ".dotm",
            ".odt", ".ott", ".fodt", ".rtf", ".sxw", ".stw"
        };

        internal static string[] GetDocumentFiles(string folder, bool recursive = false)
        {
            if (!Directory.Exists(folder)) return new string[0];
            var supported = new HashSet<string>(Extensions, StringComparer.OrdinalIgnoreCase);
            return Directory.GetFiles(folder, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                .Where(path => supported.Contains(Path.GetExtension(path)))
                .Where(path => !Path.GetFileName(path).StartsWith("~$", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => Path.GetFileName(path), StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }

        internal static bool IsSupported(string path)
        {
            string extension = Path.GetExtension(path);
            return Extensions.Any(value => string.Equals(value, extension, StringComparison.OrdinalIgnoreCase));
        }

        internal static int GetPreference(string path)
        {
            string extension = Path.GetExtension(path);
            for (int i = 0; i < Extensions.Length; i++)
                if (string.Equals(extension, Extensions[i], StringComparison.OrdinalIgnoreCase)) return i;
            return int.MaxValue;
        }

        internal static bool IsPdfCurrent(string documentPath, string pdfPath)
        {
            if (string.IsNullOrWhiteSpace(documentPath) || !File.Exists(documentPath)) return false;
            if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath)) return false;
            return File.GetLastWriteTimeUtc(pdfPath) >= File.GetLastWriteTimeUtc(documentPath);
        }

        internal static string FindSofficeConverter()
        {
            var candidates = new List<string>();
            AddOfficeCandidates(candidates, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
            AddOfficeCandidates(candidates, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));

            string fromPath = FindOnPath("soffice.exe");
            if (fromPath != null) candidates.Add(fromPath);
            return candidates.FirstOrDefault(File.Exists);
        }

        internal static string FindLocalPdfRenderer()
        {
            var candidates = new List<string>();
            AddBrowserCandidates(candidates, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
            AddBrowserCandidates(candidates, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                candidates.Add(Path.Combine(localAppData, "Microsoft", "Edge", "Application", "msedge.exe"));
                candidates.Add(Path.Combine(localAppData, "Google", "Chrome", "Application", "chrome.exe"));
            }

            string fromPath = FindOnPath("msedge.exe") ?? FindOnPath("chrome.exe") ?? FindOnPath("chromium.exe");
            if (fromPath != null) candidates.Add(fromPath);
            return candidates.FirstOrDefault(File.Exists);
        }

        private static void AddBrowserCandidates(List<string> candidates, string programFiles)
        {
            if (string.IsNullOrWhiteSpace(programFiles)) return;
            candidates.Add(Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe"));
            candidates.Add(Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"));
            candidates.Add(Path.Combine(programFiles, "Chromium", "Application", "chrome.exe"));
        }

        private static void AddOfficeCandidates(List<string> candidates, string programFiles)
        {
            if (string.IsNullOrWhiteSpace(programFiles)) return;
            candidates.Add(Path.Combine(programFiles, "LibreOffice", "program", "soffice.exe"));
            candidates.Add(Path.Combine(programFiles, "Apache OpenOffice 4", "program", "soffice.exe"));
            candidates.Add(Path.Combine(programFiles, "OpenOffice 4", "program", "soffice.exe"));
        }

        internal static string FindOnlyOfficeEditor()
        {
            var candidates = new List<string>();
            AddOnlyOfficeCandidate(candidates, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
            AddOnlyOfficeCandidate(candidates, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
            return candidates.FirstOrDefault(File.Exists);
        }

        private static void AddOnlyOfficeCandidate(List<string> candidates, string programFiles)
        {
            if (string.IsNullOrWhiteSpace(programFiles)) return;
            candidates.Add(Path.Combine(programFiles, "ONLYOFFICE", "DesktopEditors", "DesktopEditors.exe"));
        }

        private static string FindOnPath(string executableName)
        {
            string pathValue = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string folder in pathValue.Split(Path.PathSeparator))
            {
                string cleanFolder = folder.Trim().Trim('"');
                if (cleanFolder.Length == 0) continue;
                try
                {
                    string candidate = Path.Combine(cleanFolder, executableName);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }
            return null;
        }

        internal static string GetOfficeSuiteName(string executable)
        {
            if (string.IsNullOrWhiteSpace(executable)) return UiText.Get("suite bureautique", "office suite");
            if (executable.IndexOf("LibreOffice", StringComparison.OrdinalIgnoreCase) >= 0) return "LibreOffice";
            if (executable.IndexOf("OpenOffice", StringComparison.OrdinalIgnoreCase) >= 0) return "Apache OpenOffice";
            return UiText.Get("suite compatible (soffice)", "compatible suite (soffice)");
        }

        internal static void DeleteConversionFolder(string folder, string expectedParent)
        {
            if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(expectedParent)) return;
            try
            {
                string fullFolder = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
                string fullParent = Path.GetFullPath(expectedParent).TrimEnd(Path.DirectorySeparatorChar);
                string name = Path.GetFileName(fullFolder);
                if (!string.Equals(Path.GetDirectoryName(fullFolder), fullParent, StringComparison.OrdinalIgnoreCase)) return;
                if (!name.StartsWith(".procedurepilot-convert-", StringComparison.Ordinal)) return;
                if (Directory.Exists(fullFolder)) Directory.Delete(fullFolder, true);
            }
            catch { }
        }
    }
}
