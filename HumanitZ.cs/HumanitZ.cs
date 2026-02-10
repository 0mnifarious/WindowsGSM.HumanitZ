using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Reflection;
using WindowsGSM.Functions;
using WindowsGSM.GameServer.Engine;
using WindowsGSM.GameServer.Query;

namespace WindowsGSM.Plugins
{
    public class HumanitZ : SteamCMDAgent
    {
        // --------------------------------------------------------------------
        // Plugin metadata
        // --------------------------------------------------------------------
        public Plugin Plugin = new Plugin
        {
            name = "WindowsGSM.HumanitZ",
            author = "niMBis (maintained)",
            description = "WindowsGSM plugin for supporting HumanitZ Dedicated Server",
            version = "1.1.0",
            url = "https://github.com/0mnifarious/WindowsGSM.HumanitZ",
            color = "#34c9eb"
        };

        // --------------------------------------------------------------------
        // SteamCMD installer settings
        // --------------------------------------------------------------------
        public override bool loginAnonymous => true;
        public override string AppId => "2728330"; // HZ_SERVER (HumanitZ Dedicated Server) Steam App ID

        // --------------------------------------------------------------------
        // Standard constructor
        // --------------------------------------------------------------------
        public HumanitZ(ServerConfig serverData) : base(serverData) => base.serverData = _serverData = serverData;
        private readonly ServerConfig _serverData;

        // WindowsGSM reads these (Error/Notice) for UI feedback in many plugins
        public string Error, Notice;

        // --------------------------------------------------------------------
        // Fixed plugin defaults (WindowsGSM will copy these into WindowsGSM.cfg
        // on first install; user can override per-server in the UI).
        // --------------------------------------------------------------------

        // Most likely executable path for HumanitZ 1.0+ on Windows (folder renamed from TSSGame -> HumanitZServer in 1.0)
        public override string StartPath => @"HumanitZServer\Binaries\Win64\HumanitZServer-Win64-Shipping.exe";

        public string FullName = "HumanitZ Dedicated Server";

        // If the server doesn't emit meaningful stdout, WindowsGSM embed console may be quiet.
        // In that case, rely on the server's file logs (usually under a Saved\Logs directory).
        public bool AllowsEmbedConsole = true;

        // Reserve game port + a nearby extra port (some servers use adjacent ports).
        public int PortIncrements = 2;

        // Steam A2S query (uses ServerQueryPort). HumanitZ uses a Steam query port in common setups.
        public object QueryMethod = new A2S();

        // Default ports (user can override in WindowsGSM UI)
        public string Port = "7777";     // Game port
        public string QueryPort = "27015"; // Steam query port

        // WindowsGSM expects defaults for these.
        public string Defaultmap = "DedicatedSaveMP";
        public string Maxplayers = "24";

        // Leave empty by default; user can add extra args in WindowsGSM UI (WindowsGSM.cfg: serverparam)
        public string Additional = "";

        // --------------------------------------------------------------------
        // Helpers
        // --------------------------------------------------------------------
        private string GetConfigString(string propertyName, string fallback)
        {
            try
            {
                var prop = _serverData.GetType().GetProperty(
                    propertyName,
                    BindingFlags.IgnoreCase | BindingFlags.Instance | BindingFlags.Public
                );
                var val = prop?.GetValue(_serverData, null)?.ToString();
                return string.IsNullOrWhiteSpace(val) ? fallback : val;
            }
            catch
            {
                return fallback;
            }
        }

        private bool GetConfigBool(string propertyName, bool fallback)
        {
            try
            {
                var prop = _serverData.GetType().GetProperty(
                    propertyName,
                    BindingFlags.IgnoreCase | BindingFlags.Instance | BindingFlags.Public
                );
                var raw = prop?.GetValue(_serverData, null);
                if (raw == null) return fallback;
                if (raw is bool b) return b;

                // WindowsGSM historically stores many toggles as "0"/"1" strings in cfg.
                var s = raw.ToString()?.Trim();
                if (string.IsNullOrWhiteSpace(s)) return fallback;

                if (string.Equals(s, "1", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(s, "0", StringComparison.OrdinalIgnoreCase)) return false;

                if (bool.TryParse(s, out var parsed)) return parsed;
                return fallback;
            }
            catch
            {
                return fallback;
            }
        }

        private static string AppendArgIfMissing(string args, string token, string value)
        {
            // token should include trailing "=" if needed, e.g. "-port="
            if (args.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0) return args;
            return (args + " " + token + value).Trim();
        }

        private static string AppendFlagIfMissing(string args, string flag)
        {
            if (args.IndexOf(flag, StringComparison.OrdinalIgnoreCase) >= 0) return args;
            return (args + " " + flag).Trim();
        }

        private string FindExecutable(string rootDir)
        {
            // 1. Try the StartPath first (latest expected).
            var primary = Path.Combine(rootDir, StartPath);
            if (File.Exists(primary)) return primary;

            // 2. Common historical / alternative locations seen across versions and community docs.
            var candidates = new[]
            {
                // Older Unreal project naming
                @"TSSGame\Binaries\Win64\TSSGameServer-Win64-Shipping.exe",
                @"TSSGameServer.exe",

                // Some hosts/documentation refer to a wrapper exe
                @"HumanitZServer.exe",
                @"HumanitZServer\Binaries\Win64\TSSGameServer-Win64-Shipping.exe",
                @"HumanitZServer\Binaries\Win64\TSSGameServer.exe",
            };

            foreach (var rel in candidates)
            {
                var full = Path.Combine(rootDir, rel);
                if (File.Exists(full)) return full;
            }

            // 3. Last resort: scan for a shipping server executable.
            // Keep the pattern tight to avoid accidentally launching the client binary.
            var exe = Directory.EnumerateFiles(rootDir, "*Server*-Win64-Shipping.exe", SearchOption.AllDirectories)
                               .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(exe) && File.Exists(exe)) return exe;

            return null;
        }

        // --------------------------------------------------------------------
        // CreateServerCFG
        // --------------------------------------------------------------------
        public async void CreateServerCFG()
        {
            // HumanitZ commonly creates GameServerSettings.ini on first start.
            // If a REF_*.ini is shipped, we can copy it once to help first boot.
            await Task.Run(() =>
            {
                try
                {
                    var root = ServerPath.GetServersServerFiles(_serverData.ServerID);

                    // Try both known "game data" roots.
                    var probeDirs = new[]
                    {
                        Path.Combine(root, "HumanitZServer"),
                        Path.Combine(root, "TSSGame"),
                        root
                    };

                    foreach (var dir in probeDirs.Where(Directory.Exists))
                    {
                        var target = Path.Combine(dir, "GameServerSettings.ini");
                        if (File.Exists(target)) return;

                        var refIni = Directory.EnumerateFiles(dir, "*GameServerSettings.ini", SearchOption.TopDirectoryOnly)
                                              .FirstOrDefault(f =>
                                              {
                                                  var fn = Path.GetFileName(f);
                                                  return fn.StartsWith("REF", StringComparison.OrdinalIgnoreCase)
                                                      || fn.StartsWith("ref", StringComparison.OrdinalIgnoreCase);
                                              });

                        if (!string.IsNullOrWhiteSpace(refIni) && File.Exists(refIni))
                        {
                            File.Copy(refIni, target, overwrite: false);
                            Notice = $"Created {target} from {Path.GetFileName(refIni)}";
                            return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Non-fatal: config can still be created by the server at first boot.
                    Notice = $"CreateServerCFG skipped: {ex.Message}";
                }
            });
        }

        // --------------------------------------------------------------------
        // Start server
        // --------------------------------------------------------------------
        public async Task<Process> Start()
        {
            return await Task.Run(() =>
            {
                var root = ServerPath.GetServersServerFiles(_serverData.ServerID);

                var exePath = FindExecutable(root);
                if (string.IsNullOrWhiteSpace(exePath))
                {
                    Error = $"Server executable not found under: {root}. " +
                            $"Expected (primary): {StartPath}";
                    return null;
                }

                // Respect WindowsGSM's per-server embed console toggle if it exists.
                // The config key is documented as `embedconsole` in WindowsGSM.cfg.
                var embedConsoleEnabled = AllowsEmbedConsole && GetConfigBool("EmbedConsole", true);

                // WindowsGSM populates _serverData.ServerParam (cfg: serverparam).
                var args = (_serverData.ServerParam ?? string.Empty).Trim();

                // Always log; UE servers often need -log for useful output.
                args = AppendFlagIfMissing(args, "-log");

                // Ensure ports are present unless user already specified them.
                var serverPort = GetConfigString("ServerPort", Port);
                var serverQueryPort = GetConfigString("ServerQueryPort", QueryPort);

                args = AppendArgIfMissing(args, "-port=", serverPort);

                // HumanitZ docs/community typically use `queryport=27015` WITHOUT a leading dash.
                // Keep that convention, but don't duplicate if already present.
                if (args.IndexOf("queryport=", StringComparison.OrdinalIgnoreCase) < 0 &&
                    args.IndexOf("-queryport=", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    args = (args + " " + $"queryport={serverQueryPort}").Trim();
                }

                // Optional: set -steamservername from WindowsGSM server name (if available).
                var friendlyName = GetConfigString("ServerName", string.Empty);
                if (!string.IsNullOrWhiteSpace(friendlyName))
                {
                    // Only add if user didn't already supply it.
                    if (args.IndexOf("-steamservername=", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        args = (args + " " + $"-steamservername=\"{friendlyName}\"").Trim();
                    }
                }

                // Create process
                var p = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        WorkingDirectory = Path.GetDirectoryName(exePath) ?? root,
                        FileName = exePath,
                        Arguments = args,

                        UseShellExecute = false,

                        RedirectStandardInput = embedConsoleEnabled,
                        RedirectStandardOutput = embedConsoleEnabled,
                        RedirectStandardError = embedConsoleEnabled,
                        CreateNoWindow = embedConsoleEnabled,

                        WindowStyle = ProcessWindowStyle.Minimized
                    },
                    EnableRaisingEvents = true
                };

                // Record exit code in Notice for quick diagnosis.
                p.Exited += (s, e) =>
                {
                    try { Notice = $"{FullName} exited (code {p.ExitCode})."; }
                    catch { /* ignore */ }
                };

                // Hook output to WindowsGSM console when enabled
                ServerConsole serverConsole = null;
                if (embedConsoleEnabled)
                {
                    serverConsole = new ServerConsole(_serverData.ServerID);
                    p.OutputDataReceived += serverConsole.AddOutput;
                    p.ErrorDataReceived += serverConsole.AddOutput;
                }

                try
                {
                    p.Start();

                    if (embedConsoleEnabled)
                    {
                        p.BeginOutputReadLine();
                        p.BeginErrorReadLine();
                    }

                    // Quick crash-loop guard: if the process exits immediately, surface a clearer error.
                    Task.Delay(3000).Wait();
                    if (p.HasExited)
                    {
                        Error = $"{FullName} exited during startup (code {p.ExitCode}). Check logs in Saved\\Logs and WindowsGSM console.";
                        return null;
                    }

                    return p;
                }
                catch (Exception ex)
                {
                    Error = ex.Message;
                    return null;
                }
            });
        }

        // --------------------------------------------------------------------
        // Stop server
        // --------------------------------------------------------------------
        public async Task Stop(Process p)
        {
            if (p == null) return;

            await Task.Run(() =>
            {
                try
                {
                    if (p.HasExited) return;

                    // Attempt a graceful stop first:
                    // 1) If stdin is available, send common quit commands.
                    try
                    {
                        if (p.StartInfo.RedirectStandardInput)
                        {
                            p.StandardInput.WriteLine("quit");
                            p.StandardInput.WriteLine("exit");
                            p.StandardInput.Flush();
                        }
                    }
                    catch { /* ignore */ }

                    // 2) Send CTRL+C to the window (WindowsGSM helper).
                    try
                    {
                        Functions.ServerConsole.SetMainWindow(p.MainWindowHandle);
                        Functions.ServerConsole.SendWaitToMainWindow("^c");
                    }
                    catch { /* ignore */ }

                    // Wait up to 30 seconds for clean exit.
                    if (p.WaitForExit(30000)) return;

                    // Force kill (tree) as a last resort.
                    try
                    {
                        var killer = new Process
                        {
                            StartInfo = new ProcessStartInfo
                            {
                                FileName = "taskkill",
                                Arguments = $"/PID {p.Id} /T /F",
                                UseShellExecute = false,
                                RedirectStandardOutput = true,
                                RedirectStandardError = true,
                                CreateNoWindow = true
                            }
                        };
                        killer.Start();
                        killer.WaitForExit(10000);
                    }
                    catch { /* ignore */ }
                }
                catch (Exception ex)
                {
                    Error = $"Stop failed: {ex.Message}";
                }
            });
        }
    }
}
