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
        Size = new Size(560, 480);
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        BackColor = Color.FromArgb(28, 28, 30);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10);

        _headerPanel = new Panel
        {
            Location = new Point(0, 0),
            Size = new Size(560, 80),
            BackColor = Color.FromArgb(45, 45, 50)
        };

        _titleLabel = new Label
        {
            Text = "Pratfall Mod Framework",
            Location = new Point(20, 15),
            Size = new Size(520, 28),
            Font = new Font("Segoe UI", 16, FontStyle.Bold),
            ForeColor = Color.White,
            BackColor = Color.Transparent
        };

        var subtitleLabel = new Label
        {
            Text = "One-click install. Fully reversible via Uninstall or Steam Verify.",
            Location = new Point(20, 48),
            Size = new Size(520, 20),
            Font = new Font("Segoe UI", 9),
            ForeColor = Color.FromArgb(180, 180, 190),
            BackColor = Color.Transparent
        };

        _headerPanel.Controls.Add(_titleLabel);
        _headerPanel.Controls.Add(subtitleLabel);

        _gamePathLabel = new Label
        {
            Text = "Game Path:",
            Location = new Point(20, 100),
            Size = new Size(80, 24)
        };

        _gamePathBox = new TextBox
        {
            Location = new Point(100, 98),
            Size = new Size(340, 24),
            BackColor = Color.FromArgb(50, 50, 55),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            ReadOnly = true
        };

        _browseBtn = new Button
        {
            Text = "Browse...",
            Location = new Point(450, 96),
            Size = new Size(90, 28),
            BackColor = Color.FromArgb(60, 60, 65),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        _browseBtn.Click += BrowseBtn_Click;

        _installBtn = new Button
        {
            Text = "Install",
            Location = new Point(100, 140),
            Size = new Size(160, 40),
            BackColor = Color.FromArgb(0, 120, 215),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 11, FontStyle.Bold)
        };
        _installBtn.Click += InstallBtn_Click;

        _uninstallBtn = new Button
        {
            Text = "Uninstall",
            Location = new Point(280, 140),
            Size = new Size(160, 40),
            BackColor = Color.FromArgb(60, 60, 65),
            ForeColor = Color.FromArgb(200, 200, 200),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 11)
        };
        _uninstallBtn.Click += UninstallBtn_Click;

        _statusLabel = new Label
        {
            Text = "Ready",
            Location = new Point(20, 195),
            Size = new Size(520, 20),
            Font = new Font("Segoe UI", 9, FontStyle.Italic),
            ForeColor = Color.FromArgb(150, 150, 160)
        };

        _progressBar = new ProgressBar
        {
            Location = new Point(20, 220),
            Size = new Size(520, 20),
            Style = ProgressBarStyle.Continuous,
            ForeColor = Color.FromArgb(0, 120, 215),
            BackColor = Color.FromArgb(50, 50, 55)
        };

        _logBox = new RichTextBox
        {
            Location = new Point(20, 255),
            Size = new Size(520, 175),
            BackColor = Color.FromArgb(20, 20, 22),
            ForeColor = Color.FromArgb(200, 200, 210),
            BorderStyle = BorderStyle.None,
            ReadOnly = true,
            Font = new Font("Consolas", 9)
        };

        Controls.Add(_headerPanel);
        Controls.Add(_gamePathLabel);
        Controls.Add(_gamePathBox);
        Controls.Add(_browseBtn);
        Controls.Add(_installBtn);
        Controls.Add(_uninstallBtn);
        Controls.Add(_statusLabel);
        Controls.Add(_progressBar);
        Controls.Add(_logBox);
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
