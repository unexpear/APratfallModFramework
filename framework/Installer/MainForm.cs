using System.Diagnostics;
using System.Reflection;
using Microsoft.Win32;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace PratfallModFramework.Installer;

public class MainForm : Form
{
    private Label _titleLabel = null!;
    private Label _gamePathLabel = null!;
    private TextBox _gamePathBox = null!;
    private Button _browseBtn = null!;
    private Button _installBtn = null!;
    private Button _uninstallBtn = null!;
    private RichTextBox _logBox = null!;
    private ProgressBar _progressBar = null!;
    private Label _statusLabel = null!;
    private Panel _headerPanel = null!;

    private string _gameDir = "";

    public MainForm()
    {
        InitializeComponent();
        AutoDetectGamePath();
    }

    private void InitializeComponent()
    {
        Text = "Pratfall Mod Framework Installer";
        // Explicit DPI auto-scale so the layout looks the same at 100%, 125%,
        // 150%, 200%. Without this, WinForms uses Font-based scaling and the
        // absolute positions we used previously got mangled on high-DPI displays
        // (controls overlapped, status/progress/log got pushed off-screen).
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Size = new Size(700, 864);
        MinimumSize = new Size(700, 864);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        BackColor = Color.FromArgb(28, 28, 30);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10);

        // Header panel — fixed height, docked to top. Children use Absolute
        // pixel heights (not AutoSize) because Dock=Fill labels in AutoSize
        // rows collapse to a 1-pixel sliver.
        _headerPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 178,
            BackColor = Color.FromArgb(45, 45, 50),
            Padding = new Padding(24, 20, 24, 20)
        };

        var headerStack = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.Transparent
        };
        headerStack.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
        headerStack.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));

        _titleLabel = new Label
        {
            Text = "Pratfall Mod Framework",
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 16, FontStyle.Bold),
            ForeColor = Color.White,
            BackColor = Color.Transparent,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0)
        };

        var subtitleLabel = new Label
        {
            Text = "One-click install. Fully reversible via Uninstall or Steam Verify.",
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 9),
            ForeColor = Color.FromArgb(180, 180, 190),
            BackColor = Color.Transparent,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0)
        };

        headerStack.Controls.Add(_titleLabel, 0, 0);
        headerStack.Controls.Add(subtitleLabel, 0, 1);
        _headerPanel.Controls.Add(headerStack);

        // Body — TableLayoutPanel with Absolute pixel row heights so docked
        // children render at their intended size. Only the log row uses
        // Percent so it fills whatever space is left.
        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(24, 24, 24, 24),
            BackColor = Color.Transparent
        };
        // Every row gets an extra ~1 inch (96px) of vertical room over the
        // bare-minimum so controls render with breathing space at any DPI.
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 108));    // path row (textbox + browse)
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 128));    // install/uninstall buttons
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));     // status label
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));     // progress bar
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));    // log fills rest

        // Row 0 — path label + textbox + browse button.
        var pathRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 12),
            BackColor = Color.Transparent
        };
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));

        _gamePathLabel = new Label
        {
            Text = "Game Path:",
            Dock = DockStyle.Fill,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0)
        };

        _gamePathBox = new TextBox
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(50, 50, 55),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            ReadOnly = true,
            Margin = new Padding(0, 4, 8, 4)
        };

        _browseBtn = new Button
        {
            Text = "Browse...",
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(60, 60, 65),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(0, 2, 0, 2)
        };
        _browseBtn.Click += BrowseBtn_Click;

        pathRow.Controls.Add(_gamePathLabel, 0, 0);
        pathRow.Controls.Add(_gamePathBox, 1, 0);
        pathRow.Controls.Add(_browseBtn, 2, 0);

        // Row 1 — Install + Uninstall buttons in a nested 2-column TLP so they
        // share the row width evenly and stay legible at any DPI.
        var buttonRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 10, 0, 10),
            BackColor = Color.Transparent
        };
        buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));

        _installBtn = new Button
        {
            Text = "Install",
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(0, 120, 215),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            Margin = new Padding(0, 0, 8, 0)
        };
        _installBtn.Click += InstallBtn_Click;

        _uninstallBtn = new Button
        {
            Text = "Uninstall",
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(60, 60, 65),
            ForeColor = Color.FromArgb(200, 200, 200),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 11),
            Margin = new Padding(8, 0, 0, 0)
        };
        _uninstallBtn.Click += UninstallBtn_Click;

        buttonRow.Controls.Add(_installBtn, 0, 0);
        buttonRow.Controls.Add(_uninstallBtn, 1, 0);

        // Row 2 — status label.
        _statusLabel = new Label
        {
            Text = "Ready",
            Dock = DockStyle.Fill,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI", 9, FontStyle.Italic),
            ForeColor = Color.FromArgb(150, 150, 160),
            Margin = new Padding(0)
        };

        // Row 3 — progress bar.
        _progressBar = new ProgressBar
        {
            Dock = DockStyle.Fill,
            Style = ProgressBarStyle.Continuous,
            ForeColor = Color.FromArgb(0, 120, 215),
            BackColor = Color.FromArgb(50, 50, 55),
            Margin = new Padding(0, 2, 0, 6)
        };

        // Row 4 — log box fills remaining vertical space. Slight border + lighter
        // bg than form so the empty rectangle is visibly present (otherwise it
        // blends into the form background and looks like there's nothing there).
        _logBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(38, 38, 42),
            ForeColor = Color.FromArgb(220, 220, 230),
            BorderStyle = BorderStyle.FixedSingle,
            ReadOnly = true,
            Font = new Font("Consolas", 9),
            Margin = new Padding(0)
        };

        body.Controls.Add(pathRow, 0, 0);
        body.Controls.Add(buttonRow, 0, 1);
        body.Controls.Add(_statusLabel, 0, 2);
        body.Controls.Add(_progressBar, 0, 3);
        body.Controls.Add(_logBox, 0, 4);

        // Order matters: docked controls fill in reverse-Z order, so add Fill
        // before Top to ensure the body sits below the header.
        Controls.Add(body);
        Controls.Add(_headerPanel);
    }

    private void AutoDetectGamePath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Valve\Steam\Apps\4244510");
            if (key != null)
            {
                if (key.GetValue("Installed") is int installed && installed == 1)
                {
                    using var steamKey = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Valve\Steam");
                    if (steamKey?.GetValue("SteamPath") is string steamPath)
                    {
                        var libraryFoldersPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
                        if (File.Exists(libraryFoldersPath))
                        {
                            var lines = File.ReadAllLines(libraryFoldersPath);
                            foreach (var line in lines)
                            {
                                var match = System.Text.RegularExpressions.Regex.Match(line, "\"([^\"]+)\"\\s*$");
                                if (match.Success && Directory.Exists(match.Groups[1].Value))
                                {
                                    var testPath = Path.Combine(match.Groups[1].Value, "steamapps", "common", "Pratfall");
                                    if (Directory.Exists(testPath))
                                    {
                                        _gameDir = testPath;
                                        _gamePathBox.Text = testPath;
                                        Log($"Auto-detected game at: {testPath}");
                                        return;
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // Try the canonical default Steam location as a last resort. Custom drives
            // (D:, E:, etc.) are already covered by the libraryfolders.vdf scan above.
            var defaultSteam = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Steam", "steamapps", "common", "Pratfall");
            if (Directory.Exists(defaultSteam))
            {
                _gameDir = defaultSteam;
                _gamePathBox.Text = defaultSteam;
                Log($"Found game at: {defaultSteam}");
                return;
            }

            Log("Could not auto-detect game. Click Browse to locate Pratfall folder.");
        }
        catch (Exception ex)
        {
            Log($"Detection warning: {ex.Message}");
        }
    }

    private void BrowseBtn_Click(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog();
        dialog.Description = "Select Pratfall game folder (contains Pratfall.exe)";
        dialog.ShowNewFolderButton = false;
        if (dialog.ShowDialog() == DialogResult.OK)
        {
            _gameDir = dialog.SelectedPath;
            _gamePathBox.Text = _gameDir;
            Log($"Selected game path: {_gameDir}");
        }
    }

    private async void InstallBtn_Click(object? sender, EventArgs e)
    {
        if (string.IsNullOrEmpty(_gameDir) || !File.Exists(Path.Combine(_gameDir, "Pratfall.exe")))
        {
            Log("Error: Invalid game path. Select the folder containing Pratfall.exe.");
            return;
        }

        SetButtonsEnabled(false);
        _statusLabel.Text = "Installing...";
        _progressBar.Value = 0;

        try
        {
            await Task.Run(() => DoInstall());
            _statusLabel.Text = "Install complete! Launch Pratfall to use the Mod Framework.";
        }
        catch (Exception ex)
        {
            Log($"Error: {ex.Message}");
            _statusLabel.Text = "Install failed.";
        }
        finally
        {
            _progressBar.Value = 100;
            SetButtonsEnabled(true);
        }
    }

    private async void UninstallBtn_Click(object? sender, EventArgs e)
    {
        if (string.IsNullOrEmpty(_gameDir))
        {
            Log("Error: No game path selected.");
            return;
        }

        var result = MessageBox.Show(
            "Uninstall the Mod Framework?\n\n" +
            "- Original Pratfall.dll will be restored\n" +
            "- Framework DLL will be removed\n" +
            "- Your mods folder will NOT be touched",
            "Confirm Uninstall",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (result != DialogResult.Yes) return;

        SetButtonsEnabled(false);
        _statusLabel.Text = "Uninstalling...";
        _progressBar.Value = 0;

        try
        {
            await Task.Run(() => DoUninstall());
            _statusLabel.Text = "Uninstall complete.";
        }
        catch (Exception ex)
        {
            Log($"Error: {ex.Message}");
            _statusLabel.Text = "Uninstall failed.";
        }
        finally
        {
            _progressBar.Value = 100;
            SetButtonsEnabled(true);
        }
    }

    private void DoInstall()
    {
        var dataDir = Path.Combine(_gameDir, "data_Pratfall_windows_x86_64");
        var dllPath = Path.Combine(dataDir, "Pratfall.dll");
        var backupPath = Path.Combine(dataDir, "Pratfall.dll.original");
        var frameworkDest = Path.Combine(dataDir, "PratfallModFramework.dll");
        var bootstrapDest = Path.Combine(dataDir, "PratfallBootstrapLoader.dll");

        Invoke(() => _progressBar.Value = 10);
        Log("Backing up original Pratfall.dll...");

        if (!File.Exists(backupPath))
        {
            File.Copy(dllPath, backupPath, overwrite: false);
            Log("Backup created: Pratfall.dll.original");
        }
        else
        {
            Log("Backup already exists, skipping");
        }

        Invoke(() => _progressBar.Value = 20);
        Log("Deploying BootstrapLoader...");

        ExtractEmbeddedDll("PratfallBootstrapLoader.dll", bootstrapDest);

        Invoke(() => _progressBar.Value = 30);
        Log("Deploying framework DLL...");

        ExtractEmbeddedDll("PratfallModFramework.dll", frameworkDest);

        Invoke(() => _progressBar.Value = 50);
        Log("Patching Pratfall.dll (GcManager._Ready())...");

        PatchDll(dllPath, dataDir, bootstrapDest, frameworkDest);

        Invoke(() => _progressBar.Value = 80);
        Log("Creating manifest...");

        var modsDir = Path.Combine(_gameDir, "mods", "PratfallModFramework");
        Directory.CreateDirectory(modsDir);
        var manifestPath = Path.Combine(modsDir, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            File.WriteAllText(manifestPath, @"{
  ""id"": ""PratfallModFramework"",
  ""name"": ""Pratfall Mod Framework"",
  ""version"": ""1.3.0"",
  ""author"": ""ModFramework"",
  ""description"": ""Base mod framework. Enables mod detection, voting, P2P transfer, and runtime loading."",
  ""type"": ""framework"",
  ""effects"": { ""settings"": [], ""patches"": [], ""nodes"": [], ""assets"": [], ""needsRestart"": false },
  ""multiplayer"": { ""mode"": ""local_only"", ""requires"": [], ""conflictsWith"": [] }
}");
            Log("Manifest created in mods folder");
        }

        Invoke(() => _progressBar.Value = 100);
        Log("Install complete! Launch Pratfall to use the Mod Framework.");
    }

    private void ExtractEmbeddedDll(string resourceName, string destPath)
    {
        // Try embedded resource first
        var asm = Assembly.GetExecutingAssembly();
        using (var stream = asm.GetManifestResourceStream(resourceName))
        {
            if (stream != null)
            {
                using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write);
                stream.CopyTo(fs);
                Log($"Deployed {resourceName} from embedded resource");
                return;
            }
        }

        // Fallback: look next to the .exe
        var sidecarPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, resourceName);
        if (File.Exists(sidecarPath))
        {
            File.Copy(sidecarPath, destPath, overwrite: true);
            Log($"Deployed {resourceName} from sidecar");
            return;
        }

        throw new FileNotFoundException(
            $"{resourceName} not found. Build the solution first, or place it next to the installer.");
    }

    private void DoUninstall()
    {
        var dataDir = Path.Combine(_gameDir, "data_Pratfall_windows_x86_64");
        var dllPath = Path.Combine(dataDir, "Pratfall.dll");
        var backupPath = Path.Combine(dataDir, "Pratfall.dll.original");
        var frameworkPath = Path.Combine(dataDir, "PratfallModFramework.dll");
        var bootstrapPath = Path.Combine(dataDir, "PratfallBootstrapLoader.dll");

        Invoke(() => _progressBar.Value = 20);
        Log("Restoring original Pratfall.dll...");

        if (File.Exists(backupPath))
        {
            if (File.Exists(dllPath))
                File.Delete(dllPath);
            File.Move(backupPath, dllPath);
            Log("Original Pratfall.dll restored");
        }
        else
        {
            Log("No backup found. Use Steam -> Verify integrity to restore.");
        }

        Invoke(() => _progressBar.Value = 50);
        if (File.Exists(frameworkPath))
        {
            File.Delete(frameworkPath);
            Log("Framework DLL removed");
        }
        if (File.Exists(bootstrapPath))
        {
            File.Delete(bootstrapPath);
            Log("BootstrapLoader DLL removed");
        }

        Invoke(() => _progressBar.Value = 100);
        Log("Uninstall complete. Game restored to original state.");
    }

    private void PatchDll(string dllPath, string dataDir, string bootstrapDllPath, string frameworkDllPath)
    {
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(dataDir);

        using var assembly = AssemblyDefinition.ReadAssembly(dllPath, new ReaderParameters
        {
            ReadWrite = true,
            AssemblyResolver = resolver
        });

        var module = assembly.MainModule;

        var gcManager = module.Types.FirstOrDefault(t => t.Name == "GcManager");
        if (gcManager == null)
        {
            Log("ERROR: GcManager type not found in Pratfall.dll");
            return;
        }

        var readyMethod = gcManager.Methods.FirstOrDefault(m => m.Name == "_Ready");
        if (readyMethod == null)
        {
            Log("ERROR: GcManager._Ready() method not found");
            return;
        }

        // Check if already patched
        if (readyMethod.Body.Instructions.Count > 0 &&
            readyMethod.Body.Instructions[0].OpCode == OpCodes.Ldstr &&
            readyMethod.Body.Instructions[0].Operand?.ToString()?.Contains("PratfallBootstrapLoader") == true)
        {
            Log("GcManager._Ready() already patched, skipping");
            return;
        }

        var il = readyMethod.Body.GetILProcessor();
        var firstInsn = readyMethod.Body.Instructions[0];

        var objectType = module.ImportReference(typeof(object));
        var loadFileRef = module.ImportReference(
            typeof(Assembly).GetMethod("LoadFile", new[] { typeof(string) })!);
        var getTypeRef = module.ImportReference(
            typeof(Assembly).GetMethod("GetType", new[] { typeof(string) })!);
        var getMethodRef = module.ImportReference(
            typeof(Type).GetMethod("GetMethod", new[] { typeof(string) })!);
        var invokeRef = module.ImportReference(
            typeof(MethodInfo).GetMethod("Invoke", new[] { typeof(object), typeof(object[]) })!);

        il.InsertBefore(firstInsn, il.Create(OpCodes.Ldstr, bootstrapDllPath));
        il.InsertBefore(firstInsn, il.Create(OpCodes.Call, loadFileRef));
        il.InsertBefore(firstInsn, il.Create(OpCodes.Ldstr, "PratfallBootstrapLoader.Loader"));
        il.InsertBefore(firstInsn, il.Create(OpCodes.Callvirt, getTypeRef));
        il.InsertBefore(firstInsn, il.Create(OpCodes.Ldstr, "Init"));
        il.InsertBefore(firstInsn, il.Create(OpCodes.Callvirt, getMethodRef));
        il.InsertBefore(firstInsn, il.Create(OpCodes.Ldnull)); // target = null (static)
        il.InsertBefore(firstInsn, il.Create(OpCodes.Ldc_I4_1)); // args length = 1
        il.InsertBefore(firstInsn, il.Create(OpCodes.Newarr, objectType));
        il.InsertBefore(firstInsn, il.Create(OpCodes.Dup));
        il.InsertBefore(firstInsn, il.Create(OpCodes.Ldc_I4_0)); // index 0
        il.InsertBefore(firstInsn, il.Create(OpCodes.Ldstr, frameworkDllPath));
        il.InsertBefore(firstInsn, il.Create(OpCodes.Stelem_Ref));
        il.InsertBefore(firstInsn, il.Create(OpCodes.Callvirt, invokeRef));
        il.InsertBefore(firstInsn, il.Create(OpCodes.Pop));

        assembly.Write();
        Log("GcManager._Ready() patched to load BootstrapLoader successfully");
    }

    private void Log(string message)
    {
        Invoke(() =>
        {
            _logBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
            _logBox.ScrollToCaret();
            Debug.WriteLine(message);
        });
    }

    private void SetButtonsEnabled(bool enabled)
    {
        Invoke(() =>
        {
            _installBtn.Enabled = enabled;
            _uninstallBtn.Enabled = enabled;
            _browseBtn.Enabled = enabled;
        });
    }

    private new void Invoke(Action action)
    {
        if (IsHandleCreated)
            BeginInvoke(action);
    }
}
