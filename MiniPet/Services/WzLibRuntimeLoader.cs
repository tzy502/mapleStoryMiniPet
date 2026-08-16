using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace MiniPet.Services
{
    /// <summary>
    /// wzlib（WzComparerR2.WzLib.dll）运行时加载器。
    /// 修复：用户配置的 WzLibPath 原先只校验不生效（运行时永远用编译期内置 DLL）；
    /// 本加载器在 Main 最早期（任何 WzComparerR2 类型使用前）把用户配置的 DLL 加载进默认 ALC——
    /// 同名同版本先加载者胜，此后全部静态类型绑定（WzService / MapService / PaperdollService / PngEncoder）解析到用户 DLL。
    /// 未配置 / 路径无效 / AssemblyVersion 不兼容 / 加载异常 → 回退编译期内置 DLL（零行为变化）。
    /// 生效时机：下次启动（程序集加载后不可卸载，进程内不热换）。
    /// </summary>
    public static class WzLibRuntimeLoader
    {
        private const string WzLibFileName = "WzComparerR2.WzLib.dll";
        private static string? _loadedPath;
        private static string? _lastWarning;

        /// <summary>已生效的用户 wzlib DLL 绝对路径；null = 回退内置。</summary>
        public static string? LoadedPath => _loadedPath;

        /// <summary>加载结果提示（无效路径 / 版本不兼容 / 异常）；null = 无提示。</summary>
        public static string? LastWarning => _lastWarning;

        /// <summary>
        /// 在 Program.Main 最早期调用（构建 App 之前，保证先于任何 WzComparerR2 类型解析）。
        /// </summary>
        public static void TryLoadUserWzLib()
        {
            try
            {
                var config = new ConfigService().LoadWzConfig();
                if (string.IsNullOrWhiteSpace(config.WzLibPath))
                {
                    // 未配置：回退内置，无提示
                    _loadedPath = null;
                    _lastWarning = null;
                    return;
                }

                var dllPath = ResolveDllPath(config.WzLibPath);
                if (dllPath == null)
                {
                    _loadedPath = null;
                    _lastWarning = "用户配置的 wzlib 无效，已回退内置 DLL";
                    Console.Error.WriteLine($"[WzLibRuntimeLoader] {_lastWarning}");
                    return;
                }

                // 与内置 DLL 比对 AssemblyVersion：版本不一致时无法保证类型绑定解析到用户版，明确降级提示
                var builtinPath = Path.Combine(AppContext.BaseDirectory, WzLibFileName);
                var userVersion = AssemblyName.GetAssemblyName(dllPath).Version;
                var builtinVersion = AssemblyName.GetAssemblyName(builtinPath).Version;
                if (userVersion != builtinVersion)
                {
                    _loadedPath = null;
                    _lastWarning = $"用户 wzlib 版本 {userVersion} 与内置 {builtinVersion} 不兼容，已回退内置 DLL";
                    Console.Error.WriteLine($"[WzLibRuntimeLoader] {_lastWarning}");
                    return;
                }

                // 同名同版本先加载者胜：此后所有 WzComparerR2.WzLib 类型解析命中用户 DLL
                AssemblyLoadContext.Default.LoadFromAssemblyPath(dllPath);
                _loadedPath = dllPath;
                _lastWarning = null;
                Console.WriteLine($"[WzLibRuntimeLoader] 已启用用户配置 wzlib: {dllPath}");
            }
            catch (Exception ex)
            {
                _loadedPath = null;
                _lastWarning = $"wzlib 加载失败，已回退内置 DLL: {ex.Message}";
                Console.Error.WriteLine($"[WzLibRuntimeLoader] {_lastWarning}");
            }
        }

        /// <summary>解析配置路径 → DLL 绝对路径：支持「文件本身 / 目录根 / 目录下 Lib/」三种形态；无效返回 null。</summary>
        public static string? ResolveDllPath(string wzLibPath)
        {
            if (string.IsNullOrWhiteSpace(wzLibPath))
            {
                return null;
            }
            if (File.Exists(wzLibPath) && Path.GetFileName(wzLibPath).Equals(WzLibFileName, StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFullPath(wzLibPath);
            }
            if (Directory.Exists(wzLibPath))
            {
                var direct = Path.Combine(wzLibPath, WzLibFileName);
                if (File.Exists(direct))
                {
                    return Path.GetFullPath(direct);
                }
                var lib = Path.Combine(wzLibPath, "Lib", WzLibFileName);
                if (File.Exists(lib))
                {
                    return Path.GetFullPath(lib);
                }
            }
            return null;
        }
    }
}
