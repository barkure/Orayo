using System.Runtime.InteropServices;

namespace Orayo.Launcher;

internal static class NativeDialog
{
    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Unicode)]
    private struct Config
    {
        public uint Size;
        public nint Parent, Instance;
        public uint Flags, CommonButtons;
        [MarshalAs(UnmanagedType.LPWStr)] public string Title;
        public nint Icon;
        [MarshalAs(UnmanagedType.LPWStr)] public string Instruction;
        [MarshalAs(UnmanagedType.LPWStr)] public string Content;
        public uint ButtonCount;
        public nint Buttons;
        public int DefaultButton;
        public uint RadioCount;
        public nint Radios;
        public int DefaultRadio;
        public nint Verification, ExpandedInfo, ExpandedControl, CollapsedControl, FooterIcon, Footer, Callback, CallbackData;
        public uint Width;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct Button
    {
        public int Id;
        public nint Text;
    }

    public static int Show(bool chinese, string architecture, bool dotnet, bool windows)
    {
        var buttons = new List<(int Id, string Text)>();
        if (dotnet) buttons.Add((100, chinese ? "下载 .NET 运行库" : "Download .NET Runtime"));
        if (windows) buttons.Add((101, chinese ? "下载 Windows App Runtime" : "Download Windows App Runtime"));
        buttons.Add((102, chinese ? "重新检测" : "Check again"));
        var texts = new List<nint>();
        var size = Marshal.SizeOf<Button>();
        var memory = Marshal.AllocCoTaskMem(size * buttons.Count);
        try
        {
            for (var i = 0; i < buttons.Count; i++)
            {
                var text = Marshal.StringToCoTaskMemUni(buttons[i].Text);
                texts.Add(text);
                Marshal.StructureToPtr(new Button { Id = buttons[i].Id, Text = text }, memory + size * i, false);
            }
            var missing = new List<string>();
            if (dotnet) missing.Add($".NET Desktop Runtime {RuntimeRequirements.MinimumDotnetVersion}+ ({architecture})");
            if (windows) missing.Add($"Windows App Runtime {RuntimeRequirements.WindowsRuntimeVersion}+ ({architecture})");
            var config = new Config
            {
                Size = (uint)Marshal.SizeOf<Config>(), Flags = 8, CommonButtons = 8,
                Title = "Orayo", Instruction = chinese ? "需要安装运行库" : "Runtime installation required",
                Content = string.Join("\n", missing) + "\n\n" + (chinese
                    ? "点击下载按钮打开微软官方安装入口。安装完成后点击“重新检测”，即可启动 Orayo。"
                    : "Download the missing runtimes from Microsoft. After installing, select Check again to start Orayo."),
                ButtonCount = (uint)buttons.Count, Buttons = memory, DefaultButton = 102
            };
            var result = TaskDialogIndirect(ref config, out var selected, out _, out _);
            Marshal.ThrowExceptionForHR(result);
            return selected;
        }
        finally
        {
            foreach (var text in texts) Marshal.FreeCoTaskMem(text);
            Marshal.FreeCoTaskMem(memory);
        }
    }

    public static void Error(string title, string message) => MessageBoxW(0, message, title, 0x10);

    [DllImport("comctl32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int TaskDialogIndirect(ref Config config, out int button, out int radio, out int verified);
    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint parent, string text, string caption, uint type);
}
