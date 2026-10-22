using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        AllowDrop = true;

        // 预填默认路径
        string[] candidates = {
            @"D:\Program Files\PCL2\Plain Craft Launcher 2.exe",
            @"C:\Program Files\PCL2\Plain Craft Launcher 2.exe",
            @"C:\Program Files (x86)\PCL2\Plain Craft Launcher 2.exe"
        };
        foreach (var p in candidates)
            if (File.Exists(p)) { TxtPath.Text = p; break; }

        BtnClose.Click += (s, e) => Close();
        BtnMin.Click += (s, e) => WindowState = WindowState.Minimized;
        BtnBrowse.Click += (s, e) =>
        {
            var dlg = new OpenFileDialog { Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*" };
            if (dlg.ShowDialog() == true) SetTargetPath(dlg.FileName);
        };
        BtnPatch.Click += (s, e) => RunPatch(false);
        BtnRestore.Click += (s, e) => RunPatch(true);

        DragEnter += (s, e) =>
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effects = DragDropEffects.Copy;
        };
        Drop += (s, e) =>
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files != null && files.Length > 0) SetTargetPath(files[0]);
        };

        TxtPath.TextChanged += (s, e) => UpdateBadge();
        UpdateBadge();
    }

    private void PanTitle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void SetTargetPath(string path)
    {
        // 解析 .lnk 快捷方式
        if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            string target = TargetDetect.ResolveShortcut(path);
            if (target != null) path = target;
        }
        TxtPath.Text = path;
    }

    private void UpdateBadge()
    {
        string path = TxtPath.Text.Trim().Trim('"');
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            Badge.Visibility = Visibility.Collapsed;
            return;
        }
        try
        {
            var info = TargetDetect.Identify(File.ReadAllBytes(path));
            if (info.Kind == TargetDetect.Kind.Unknown)
            {
                Badge.Visibility = Visibility.Collapsed;
                return;
            }
            BadgeText.Text = info.Desc;
            Badge.Visibility = Visibility.Visible;
            // 配色：PCL2 蓝、PCL-CE 紫、HMCL 橙
            string bg, fg;
            switch (info.Kind)
            {
                case TargetDetect.Kind.PclCE: bg = "ColorBrush7"; fg = "ColorBrush2"; break;
                case TargetDetect.Kind.Hmcl: bg = "#FFF3E0"; fg = "#E65100"; break;
                default: bg = "ColorBrush6"; fg = "ColorBrush2"; break;
            }
            Badge.Background = (Brush)FindResource(bg);
            BadgeText.Foreground = fg.StartsWith("#") ? new SolidColorBrush((Color)ColorConverter.ConvertFromString(fg)) : (Brush)FindResource(fg);
        }
        catch { Badge.Visibility = Visibility.Collapsed; }
    }

    private void RunPatch(bool restore)
    {
        string path = TxtPath.Text.Trim().Trim('"');
        if (string.IsNullOrEmpty(path)) { Log("请先指定启动器路径。"); return; }
        if (!File.Exists(path)) { Log("文件不存在：" + path); return; }

        BtnPatch.IsEnabled = BtnRestore.IsEnabled = false;
        Cursor = Cursors.Wait;

        var worker = new System.Threading.Thread(() =>
        {
            try
            {
                if (restore) PatchRunner.Restore(path, Log, Confirm);
                else PatchRunner.Run(path, Log, Confirm);
                Log(restore ? "还原完成。" : "补丁完成。");
            }
            catch (Exception ex)
            {
                Log("失败：" + ex.Message);
            }
            Dispatcher.Invoke(() =>
            {
                BtnPatch.IsEnabled = BtnRestore.IsEnabled = true;
                Cursor = Cursors.Arrow;
            });
        }) { IsBackground = true };
        worker.Start();
    }

    private bool Confirm(string msg)
    {
        return Dispatcher.Invoke(() => MessageBox.Show(this, msg, "确认", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);
    }

    private void Log(string s)
    {
        Dispatcher.Invoke(() => { LabLog.Text += s + "\r\n"; });
    }
}
