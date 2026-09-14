using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using MiniPet;
using MiniPet.Services;
using MiniPet.Views;

namespace MiniPet.CrashSelfTest;

/// <summary>
/// 崩溃自检第二阶段：真实 WZ + 真实 SettingsWindow 预览全流程复现。
/// 复现 run32-34 的 ObjectDisposedException（CroppedBitmap 源位图被 Dispose 后布局测量）。
/// 抛异常 = 崩溃路径仍存在；正常返回 = 通过。
/// </summary>
public static class SettingsPreviewSelfTest
{
    /// <summary>在已初始化的 Headless 环境执行设置窗口预览全流程（失败抛异常）。</summary>
    public static void RunAll()
    {
        // 1. 加载真实 WZ
        var config = new ConfigService();
        var wzConfig = config.LoadWzConfig();
        var wz = new WzService();
        var (ok, wzErr) = wz.LoadWz(wzConfig.WzLibPath, wzConfig.BaseWzPath);
        Console.WriteLine($"[自检] WZ 加载: {ok} (err={wzErr})");
        if (!ok)
        {
            Console.WriteLine("[自检] WZ 加载失败——跳过真实流程，仅跑结构检查");
        }

        var cache = new CacheManager();
        var sprite = new SpriteService(cache);
        var balloon = new BalloonService(cache, wz);
        var paperdoll = new PaperdollService(wz, cache, sprite);
        var map = new MapService(wz, cache);

        // 2. 构造 SettingsWindow（构建全部 tab）
        // V0.1.3 多桌宠：SettingsWindow 构造函数新增 3 参（petManager / eventBus / targetPetId）。
        // EventBus 供 PetManager 订阅命令事件（AddPet / RemovePet / SetPrimaryPet）；PetManager 构造只订阅事件，
        // 不创建任何窗口（窗口创建在 CreateAllFromConfig 里，本自检不调用），故用最小 ServiceProvider 装配即可。
        var eventBus = new EventBus();
        var services = new ServiceCollection().BuildServiceProvider();
        var petManager = new PetManager(services, config, eventBus, wz);
        var targetPetId = GetTargetPetId(config);
        Console.WriteLine("[自检] 构造 SettingsWindow…");
        var win = new SettingsWindow(config, wz, paperdoll, map, balloon, petManager, eventBus, targetPetId);
        win.Show();

        // 3. 素材 tab 预览流程（反射驱动）
        var updatePreview = GetMethod(win, "UpdatePreviewContent");
        var loadAction = GetMethod(win, "LoadPreviewActionAsync");
        var rebuild = GetMethod(win, "RebuildPreviewSourceBitmap");

        // 用真实 mob id 触发预览（WZ 已加载）
        var mobId = "9602606";
        Console.WriteLine($"[自检] 素材预览: mob={mobId}");
        updatePreview.Invoke(win, new object[] { "测试宠物", mobId });

        // pump 后台解码 + 预览动画
        Pump(TimeSpan.FromMilliseconds(1500));

        // 4. 切动作（模拟动作按钮点击路径：直接重建源）
        Console.WriteLine("[自检] 切换动作 stand1→walk1→jump…");
        foreach (var action in new[] { "stand1", "walk1", "jump", "swingOF" })
        {
            loadAction.Invoke(win, new object[] { mobId, action, null });
            Pump(TimeSpan.FromMilliseconds(800));
            Layout(win);
        }

        // 5. 模拟气泡 tab 预览 tick（Balloon.cs 遍历 previewSprite 的路径）
        Console.WriteLine("[自检] 气泡 tab 预览 tick 混合…");
        for (int i = 0; i < 30; i++)
        {
            // 触发预览动画 timer tick（DispatcherTimer 需要真实时间推进）
            Pump(TimeSpan.FromMilliseconds(50));
            Layout(win);
            Dispatcher.UIThread.RunJobs();
        }

        // 6. 重建源（对应素材 tab 切换宠物/动作触发 Rebuild）
        Console.WriteLine("[自检] 重建预览源 × 20 轮…");
        for (int i = 0; i < 20; i++)
        {
            rebuild.Invoke(win, null);
            Layout(win);
            Dispatcher.UIThread.RunJobs();
        }

        // 7. 快速交替：加载动作 + 重建 + 布局（模拟用户快速操作）
        Console.WriteLine("[自检] 快速交替压测 × 40 轮…");
        var actions = new[] { "stand1", "walk1", "alert", "jump" };
        for (int i = 0; i < 40; i++)
        {
            loadAction.Invoke(win, new object[] { mobId, actions[i % actions.Length], null });
            Pump(TimeSpan.FromMilliseconds(120));
            Layout(win);
            Dispatcher.UIThread.RunJobs();
        }

        Console.WriteLine("=== 自检通过：SettingsWindow 预览全流程无崩溃 ===");
        win.Close();
    }

    private static void Pump(TimeSpan duration)
    {
        var until = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
    }

    private static void Layout(Window win)
    {
        win.Measure(new Avalonia.Size(1020, 740));
        win.Arrange(new Rect(0, 0, 1020, 740));
    }

    private static MethodInfo GetMethod(object obj, string name)
        => obj.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)
           ?? throw new MissingMethodException(obj.GetType().FullName, name);

    /// <summary>
    /// 取设置中心要编辑的目标宠物 id（V0.1.3 多桌宠）：配置里第一只宠物的 id，取不到则退回 "pet-1"。
    /// 与 SettingsWindow.TargetPet() 的兜底一致（id 失效时它会退回主宠 / 第一只，不会异常）。
    /// </summary>
    private static string GetTargetPetId(ConfigService config)
    {
        try
        {
            var first = config.LoadSettings().Pets?.FirstOrDefault();
            if (!string.IsNullOrEmpty(first?.Id))
            {
                return first!.Id;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[自检] 读取目标宠物 id 失败，回退 pet-1: {ex.Message}");
        }
        return "pet-1";
    }
}
