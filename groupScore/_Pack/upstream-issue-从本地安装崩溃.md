# 给 ClassIsland 提的 issue（草稿，可直接粘贴到 GitHub）

> 提交地址：https://github.com/ClassIsland/ClassIsland/issues/new
> 需要 GitHub 账号。发之前先搜一遍 `GetItemAt`，确认没人报过同样的。

---

## 标题

`[Bug] 「从本地安装」插件时抛 GetItemAt failed（0x80004002），安装流程完全不可用`

## 正文

**环境**

- ClassIsland 2.1.0.1
- Windows（触发问题的是一台测试机，见下方说明）
- 内置 Avalonia 11.3.17

**复现步骤**

1. 打开【应用设置】→【插件】
2. 点右上角 `···` →【从本地安装】
3. 在弹出的文件对话框里选中一个 `.cipx`，点「打开」

**实际结果**

弹出一个未处理异常的错误框（带 TraceID），插件装不上。异常：

```
System.Runtime.InteropServices.COMException (0x80004005): 不支持此接口
---> System.Runtime.InteropServices.COMException (0x80004002): GetItemAt failed
   at Avalonia.Win32.Win32Com.Impl.__MicroComIShellItemArrayProxy.GetItemAt(Int32 dwIndex)
   at Avalonia.Win32.Win32StorageProvider.<>c__DisplayClass14_0`1.<ShowFilePicker>b__0()
   at System.Threading.Tasks.Task`1.InnerInvoke()
   at Avalonia.Win32.Win32StorageProvider.OpenFilePickerAsync(FilePickerOpenOptions options)
   at ClassIsland.Platforms.Abstraction.Stubs.Services.AvaloniaDefaultPlatformFilePickerService.OpenFilesPickerAsync(FilePickerOpenOptions options, TopLevel root) in /_/ClassIsland.Platforms.Abstractions/Stubs/Services/AvaloniaDefaultPlatformFilePickerService.cs:line 13
   at ClassIsland.Views.SettingPages.PluginsSettingsPage.ButtonInstallFromLocal_OnClick(Object sender, RoutedEventArgs e) in /_/ClassIsland/Views/SettingPages/PluginsSettingsPage.axaml.cs:line 407
```

TraceID：`89f408032df44812876c70981eeaa9de`

**期望结果**

选中文件后正常开始安装；即使文件选择器出错，也不应该把未处理异常抛到全局处理器弹错误框。

## 我做的排查（供参考）

1. **异常发生在「打开对话框」这一步，包还没被读取** —— 调用链里没有任何插件加载代码，
   所以这与具体插件无关，任何 `.cipx` 都会触发。

2. **只有多选才会走到这个分支。** 看 Avalonia 11.3.17 的
   `src/Windows/Avalonia.Win32/Win32StorageProvider.cs`：

   ```csharp
   if (allowMultiple)
   {
       using var fileOpenDialog = frm.QueryInterface<IFileOpenDialog>();
       var shellItemArray = fileOpenDialog.Results;
       var count = shellItemArray.Count;
       for (int i = 0; i < count; i++)
       {
           var shellItem = shellItemArray.GetItemAt(i);   // ← 崩在这里
           ...
       }
   }
   else if (frm.Result is { } shellItem && GetParsingName(shellItem) is { } singleResult)
   {
       result = [convert(singleResult)];                   // ← 单选走 `IFileDialog::GetResult`，不碰数组
   }
   ```

   `AvaloniaDefaultPlatformFilePickerService.OpenFilesPickerAsync` 传的是
   `AllowMultiple = true`，于是走上面第一个分支。

3. **同样的堆栈在别的 Avalonia 项目里出现过**：UABEA issue #368（2024-08）。
   那边的结论是：按 MSDN，`IShellItemArray::GetItemAt` 只应在下标越界时失败，
   而循环范围是 `0..Count-1`，理论上不该报错；作者倾向于是**那台机器的 shell/COM 环境问题**，
   并建议暂时绕开文件选择器。

4. 因此我判断这是**特定机器环境 + 多选对话框**的组合问题，而非 ClassIsland 自身的逻辑错误。
   但在老师的一体机上「装不上插件」的后果和逻辑错误是一样的。

## 建议的修复（两处，都很小）

1. **`PluginsSettingsPage.ButtonInstallFromLocal_OnClick` 里改成 `AllowMultiple = false`。**
   这个按钮的实际用途是「选一个插件包装上」，本来就不需要多选 —— 一改就绕开了整个数组分支。
2. **给 `OpenFilesPickerAsync` 套一层 `try/catch`**，出错时按「用户取消」处理（返回空列表）并提示，
   而不是把未处理异常交给全局处理器。这样即使在某些机器上仍然打不开，也不会弹错误框吓人。

## 临时规避（已在我的插件文档里写明）

把 `.cipx` 解压到 `data\Plugins\<插件 id>\` 后重启，可正常加载 —— 这条路不经过文件选择器，
已在出问题的机器上验证可行。
