using System.Diagnostics;
using System.Drawing;
using System.Text;

namespace Bf3VietHoaGui;

internal sealed class MainForm : Form
{
    private readonly GuiSettings _settings;
    private readonly TextBox _gameBox = new() { Dock = DockStyle.Fill };
    private readonly TextBox _projectBox = new() { Dock = DockStyle.Fill };
    private readonly TextBox _sheetBox = new() { Dock = DockStyle.Fill };
    private readonly Label _filesStatus = new() { AutoSize = true, ForeColor = Color.FromArgb(80, 91, 107) };
    private readonly Label _taskStatus = new() { AutoSize = true, Text = "Sẵn sàng", ForeColor = Color.FromArgb(30, 64, 111) };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Style = ProgressBarStyle.Marquee, Visible = false };
    private readonly TextBox _log = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
        ScrollBars = ScrollBars.Vertical, BackColor = Color.FromArgb(25, 34, 49),
        ForeColor = Color.FromArgb(225, 234, 244), Font = new Font("Consolas", 9f) };
    private readonly List<Button> _actionButtons = [];
    private bool _busy;

    public MainForm()
    {
        Text = "Battlefield 3 Việt hóa";
        MinimumSize = new Size(900, 680);
        Size = new Size(1020, 760);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10f);
        BackColor = Color.FromArgb(247, 249, 252);
        AutoScaleMode = AutoScaleMode.Font;

        var workspace = FindWorkspaceRoot();
        _settings = GuiSettings.Load();
        _gameBox.Text = string.IsNullOrWhiteSpace(_settings.GameDirectory)
            ? @"C:\Program Files (x86)\Steam\steamapps\common\Battlefield 3" : _settings.GameDirectory;
        _projectBox.Text = string.IsNullOrWhiteSpace(_settings.ExportDirectory)
            ? workspace : _settings.ExportDirectory;
        _sheetBox.Text = string.IsNullOrWhiteSpace(_settings.SheetPath)
            ? Path.Combine(_projectBox.Text, "translations", "us_loc_en_vi.csv") : _settings.SheetPath;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22),
            ColumnCount = 1, RowCount = 5 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 190));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 213));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        root.Controls.Add(CreateHeader(), 0, 0);
        root.Controls.Add(CreatePathsPanel(), 0, 1);
        root.Controls.Add(CreateActionsPanel(), 0, 2);
        root.Controls.Add(CreateStatusPanel(), 0, 3);
        root.Controls.Add(CreateLogPanel(), 0, 4);
        foreach (var box in new[] { _gameBox, _projectBox, _sheetBox }) box.TextChanged += (_, _) => UpdateFileStatus();
        FormClosing += (_, _) => SaveSettings();
        UpdateFileStatus();
        AppendLog("Chọn thư mục game, thư mục dự án và file CSV. GUI không tự mở Battlefield 3.");
    }

    private Control CreateHeader()
    {
        var panel = new Panel { Dock = DockStyle.Fill };
        panel.Controls.Add(new Label { Text = "Battlefield 3 Việt hóa", Font = new Font("Segoe UI Semibold", 21f),
            ForeColor = Color.FromArgb(24, 46, 78), AutoSize = true, Location = new Point(0, 0) });
        panel.Controls.Add(new Label { Text = "Xuất dữ liệu, dịch trong CSV, rồi cài text và font cho bản Steam x86.",
            ForeColor = Color.FromArgb(85, 99, 119), AutoSize = true, Location = new Point(2, 46) });
        return panel;
    }

    private Control CreatePathsPanel()
    {
        var group = new GroupBox { Text = "Đường dẫn", Dock = DockStyle.Fill,
            Padding = new Padding(14, 12, 14, 8) };
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 4 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 147));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 106));
        for (var i = 0; i < 3; i++) grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        AddPathRow(grid, 0, "Thư mục game", _gameBox, "Chọn...", () => BrowseFolder(_gameBox));
        AddPathRow(grid, 1, "Thư mục dự án", _projectBox, "Chọn...", () => BrowseFolder(_projectBox));
        AddPathRow(grid, 2, "CSV bản dịch", _sheetBox, "Chọn...", BrowseSheet);
        grid.Controls.Add(_filesStatus, 1, 3);
        grid.SetColumnSpan(_filesStatus, 2);
        group.Controls.Add(grid);
        return group;
    }

    private static void AddPathRow(TableLayoutPanel grid, int row, string label, TextBox box,
        string buttonText, Action browse)
    {
        grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left,
            ForeColor = Color.FromArgb(44, 58, 77) }, 0, row);
        box.Margin = new Padding(0, 4, 9, 4);
        grid.Controls.Add(box, 1, row);
        var button = new Button { Text = buttonText, Dock = DockStyle.Fill, Margin = new Padding(0, 3, 0, 3) };
        button.Click += (_, _) => browse();
        grid.Controls.Add(button, 2, row);
    }

    private Control CreateActionsPanel()
    {
        var group = new GroupBox { Text = "Các bước", Dock = DockStyle.Fill, Padding = new Padding(14, 12, 14, 9) };
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        grid.Controls.Add(ActionCard("1  Xuất text", "Tạo TSV để xem và CSV để dịch. CSV cũ được giữ nguyên.", ExportText), 0, 0);
        grid.Controls.Add(ActionCard("2  Xuất font", "Xuất GFX và manifest. Font đã chỉnh sửa được giữ nguyên.", ExportFont), 1, 0);
        grid.Controls.Add(ActionCard("3  Kiểm tra CSV", "Kiểm tra ID, text gốc và số dòng đã dịch.", ValidateSheet), 0, 1);
        grid.Controls.Add(ActionCard("4  Cài bản dịch", "Dựng text, bảng mã, font; sao lưu rồi cài khi game đã thoát.", InstallTranslation), 1, 1);
        group.Controls.Add(grid);
        return group;
    }

    private Control ActionCard(string title, string description, Action action)
    {
        var panel = new Panel { Dock = DockStyle.Fill, Margin = new Padding(3, 3, 9, 7),
            BackColor = Color.White, Padding = new Padding(12) };
        var button = new Button { Text = title, Location = new Point(12, 9), Size = new Size(160, 33),
            BackColor = Color.FromArgb(34, 102, 175), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        button.FlatAppearance.BorderSize = 0;
        button.Click += (_, _) => action();
        _actionButtons.Add(button);
        panel.Controls.Add(button);
        panel.Controls.Add(new Label { Text = description, AutoSize = false, Location = new Point(181, 10),
            Width = 275, Height = 46, ForeColor = Color.FromArgb(70, 82, 99) });
        return panel;
    }

    private Control CreateStatusPanel()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 185));
        _taskStatus.Anchor = AnchorStyles.Left;
        panel.Controls.Add(_taskStatus, 0, 0);
        panel.Controls.Add(_progress, 1, 0);
        return panel;
    }

    private Control CreateLogPanel()
    {
        var group = new GroupBox { Text = "Nhật ký", Dock = DockStyle.Fill, Padding = new Padding(10, 8, 10, 10) };
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2 };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 33));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
        toolbar.Controls.Add(SmallButton("Mở CSV", OpenSheet));
        toolbar.Controls.Add(SmallButton("Mở thư mục dự án", () => OpenPath(_projectBox.Text)));
        toolbar.Controls.Add(SmallButton("Khôi phục lần cài trước", RestorePrevious));
        panel.Controls.Add(toolbar, 0, 0);
        panel.Controls.Add(_log, 0, 1);
        group.Controls.Add(panel);
        return group;
    }

    private Button SmallButton(string text, Action action)
    {
        var button = new Button { Text = text, AutoSize = true, Height = 28, Margin = new Padding(0, 0, 7, 0) };
        button.Click += (_, _) => action();
        _actionButtons.Add(button);
        return button;
    }

    private void BrowseFolder(TextBox destination)
    {
        using var dialog = new FolderBrowserDialog { ShowNewFolderButton = true,
            SelectedPath = Directory.Exists(destination.Text) ? destination.Text : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var oldProject = _projectBox.Text;
        destination.Text = dialog.SelectedPath;
        if (destination == _projectBox && _sheetBox.Text.StartsWith(oldProject + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            _sheetBox.Text = Path.Combine(dialog.SelectedPath, "translations", "us_loc_en_vi.csv");
    }

    private void BrowseSheet()
    {
        using var dialog = new SaveFileDialog { Filter = "CSV UTF-8 (*.csv)|*.csv|Tất cả file (*.*)|*.*",
            FileName = Path.GetFileName(_sheetBox.Text),
            InitialDirectory = Directory.Exists(Path.GetDirectoryName(_sheetBox.Text))
                ? Path.GetDirectoryName(_sheetBox.Text) : _projectBox.Text,
            OverwritePrompt = false };
        if (dialog.ShowDialog(this) == DialogResult.OK) _sheetBox.Text = dialog.FileName;
    }

    private void UpdateFileStatus()
    {
        var game = Path.Combine(_gameBox.Text, "Update", "Patch", "Data", "Win32", "Loc", "en.sb");
        var gameOkay = File.Exists(game);
        var sheetOkay = File.Exists(_sheetBox.Text);
        var fontOkay = File.Exists(Path.Combine(_projectBox.Text, "dump", "fonts", "en", "manifest.json"));
        _filesStatus.Text = $"Game: {(gameOkay ? "đã tìm thấy" : "chưa thấy en.sb")}   •   CSV: {(sheetOkay ? "đã có" : "chưa có")}   •   Font: {(fontOkay ? "đã xuất" : "chưa xuất")}";
    }

    private void ExportText()
    {
        if (!TryCapturePaths(out var paths)) return;
        var (game, project, sheet) = paths;
        RunOperation("Xuất text", () =>
        {
            var baseline = BaselineOrGame(game, project);
            var output = Path.Combine(project, "dump", "us_loc_en.tsv");
            var result = Bf3TextDump.Program.Run([baseline, "-o", output, "-s", sheet]);
            if (result != 0) throw new InvalidDataException("Xuất text không thành công. Xem nhật ký bên dưới.");
            return $"Đã xuất text: {output}\r\nCSV để dịch: {sheet}";
        });
    }

    private void ExportFont()
    {
        if (!TryCapturePaths(out var paths)) return;
        var (game, project, _) = paths;
        RunOperation("Xuất font", () =>
        {
            var output = Path.Combine(project, "dump", "fonts", "en");
            var result = Bf3FontDump.Program.Run([game, "-o", output]);
            if (result != 0) throw new InvalidDataException("Xuất font không thành công. Xem nhật ký bên dưới.");
            return $"Đã xuất font vào: {output}\r\nCác GFX đã chỉnh sửa trong thư mục này được giữ nguyên.";
        });
    }

    private void ValidateSheet()
    {
        if (!TryCapturePaths(out var paths)) return;
        var (game, project, sheet) = paths;
        RunOperation("Kiểm tra CSV", () =>
        {
            if (!File.Exists(sheet)) throw new FileNotFoundException("Chưa có CSV. Hãy bấm Xuất text trước.", sheet);
            var baseline = BaselineOrGame(game, project);
            var result = Bf3TextDump.Program.Run([baseline, "--validate", "-s", sheet]);
            if (result != 0) throw new InvalidDataException("CSV chưa hợp lệ. Xem nhật ký bên dưới.");
            return "ID và dữ liệu gốc trong CSV hợp lệ. Bước cài sẽ kiểm tra bảng mã, font và dung lượng gói text.";
        });
    }

    private void InstallTranslation()
    {
        if (!TryCapturePaths(out var paths)) return;
        var (game, project, sheet) = paths;
        RunOperation("Cài bản dịch", () =>
        {
            if (Process.GetProcessesByName("bf3").Length != 0)
                throw new InvalidOperationException("Hãy thoát Battlefield 3 trước khi cài bản dịch.");
            if (!File.Exists(sheet)) throw new FileNotFoundException("Chưa có CSV bản dịch", sheet);
            var gameLoc = Path.Combine(game, "Update", "Patch", "Data", "Win32", "Loc");
            var backupDir = Path.Combine(project, "build", "Loc", "backup");
            Directory.CreateDirectory(backupDir);
            var baseline = Path.Combine(backupDir, "en.sb");
            var baselineToc = Path.Combine(backupDir, "en.toc");
            if (!File.Exists(baseline)) File.Copy(Path.Combine(gameLoc, "en.sb"), baseline);
            if (!File.Exists(baselineToc)) File.Copy(Path.Combine(gameLoc, "en.toc"), baselineToc);
            var stage = Path.Combine(project, "build", "Loc", "gui_stage");
            var textResult = Bf3TextDump.Program.Run([baseline, "--import", "--map-vietnamese",
                "-s", sheet, "--stage", stage]);
            if (textResult != 0) throw new InvalidDataException("Không dựng được text và histogram. Xem nhật ký.");

            var fontManifest = Path.Combine(project, "dump", "fonts", "en", "manifest.json");
            if (!File.Exists(fontManifest))
            {
                var exportResult = Bf3FontDump.Program.Run([game, "-o", Path.GetDirectoryName(fontManifest)!]);
                if (exportResult != 0) throw new InvalidDataException("Không xuất được font nguồn. Xem nhật ký.");
            }
            var fontStage = Path.Combine(project, "build", "fonts", "gui_stage");
            Bf3FontSubset.Program.Run(["--manifest", fontManifest, "--corpus", sheet,
                "--vietnamese-map", Path.Combine(stage, "vietnamese_map.json"),
                "--output-dir", fontStage]);
            var hook = FindHookDll(project);
            var backups = Path.Combine(project, "build", "Loc", "gui_install_backups");
            var snapshot = InstallationService.Install(game, stage, fontStage, hook, backups);

            var standardStage = Path.Combine(project, "build", "Loc");
            File.Copy(Path.Combine(stage, "en.sb"), Path.Combine(standardStage, "en.sb"), true);
            File.Copy(Path.Combine(stage, "en.toc"), Path.Combine(standardStage, "en.toc"), true);
            File.Copy(Path.Combine(stage, "vietnamese_map.json"),
                Path.Combine(standardStage, "vietnamese_map.json"), true);
            return $"Đã cài text và font tiếng Việt. Sao lưu trạng thái trước: {snapshot}\r\nBạn tự mở BF3 để kiểm tra; GUI không chạy game.";
        });
    }

    private void RestorePrevious()
    {
        if (!TryCapturePaths(out var paths)) return;
        var (game, project, _) = paths;
        RunOperation("Khôi phục", () =>
        {
            var backups = Path.Combine(project, "build", "Loc", "gui_install_backups");
            var restored = InstallationService.RestoreLatest(game, backups);
            return $"Đã khôi phục file từ: {restored}";
        });
    }

    private void OpenSheet() => OpenPath(_sheetBox.Text);

    private void OpenPath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            MessageBox.Show(this, "Chưa tìm thấy đường dẫn này. Hãy xuất dữ liệu trước.", "Chưa có file",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Không mở được", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private bool TryCapturePaths(out (string Game, string Project, string Sheet) paths)
    {
        paths = default;
        if (string.IsNullOrWhiteSpace(_gameBox.Text) || string.IsNullOrWhiteSpace(_projectBox.Text) ||
            string.IsNullOrWhiteSpace(_sheetBox.Text))
        {
            MessageBox.Show(this, "Hãy điền đủ ba đường dẫn trước.", "Thiếu đường dẫn",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }
        try
        {
            paths = (Path.GetFullPath(_gameBox.Text.Trim()), Path.GetFullPath(_projectBox.Text.Trim()),
                Path.GetFullPath(_sheetBox.Text.Trim()));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            MessageBox.Show(this, ex.Message, "Đường dẫn không hợp lệ", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        SaveSettings();
        return true;
    }

    private static string BaselineOrGame(string game, string project)
    {
        var backup = Path.Combine(project, "build", "Loc", "backup", "en.sb");
        return File.Exists(backup) ? backup : Path.Combine(game, "Update", "Patch", "Data", "Win32", "Loc", "en.sb");
    }

    private static string FindHookDll(string project)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "hook", "dinput8.dll"),
            Path.Combine(project, "Bf3FontHookNative", "build-x86", "Release", "dinput8.dll"),
            Path.Combine(FindWorkspaceRoot(), "Bf3FontHookNative", "build-x86", "Release", "dinput8.dll")
        };
        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("Không tìm thấy hook dinput8.dll x86 đã build.");
    }

    private void RunOperation(string title, Func<string> action)
    {
        if (_busy) return;
        _busy = true;
        foreach (var button in _actionButtons) button.Enabled = false;
        _progress.Visible = true;
        _taskStatus.Text = $"Đang {title.ToLowerInvariant()}...";
        AppendLog($"\r\n[{DateTime.Now:HH:mm:ss}] {title}");
        _ = RunAsync();

        async Task RunAsync()
        {
            var output = new StringWriter();
            var oldOut = Console.Out;
            var oldError = Console.Error;
            try
            {
                var result = await Task.Run(() =>
                {
                    Console.SetOut(output);
                    Console.SetError(output);
                    try { return action(); }
                    finally { Console.SetOut(oldOut); Console.SetError(oldError); }
                });
                AppendLog(output.ToString());
                AppendLog(result);
                _taskStatus.Text = "Hoàn tất";
            }
            catch (Exception ex)
            {
                AppendLog(output.ToString());
                AppendLog("LỖI: " + ex.Message);
                _taskStatus.Text = "Có lỗi — xem nhật ký";
                MessageBox.Show(this, ex.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _busy = false;
                _progress.Visible = false;
                foreach (var button in _actionButtons) button.Enabled = true;
                UpdateFileStatus();
            }
        }
    }

    private void AppendLog(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        _log.AppendText(text.TrimEnd() + Environment.NewLine);
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }

    private void SaveSettings()
    {
        _settings.GameDirectory = _gameBox.Text.Trim();
        _settings.ExportDirectory = _projectBox.Text.Trim();
        _settings.SheetPath = _sheetBox.Text.Trim();
        _settings.Save();
    }

    private static string FindWorkspaceRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Bf3TextDump", "Bf3TextDump.csproj")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }
        return Environment.CurrentDirectory;
    }
}
