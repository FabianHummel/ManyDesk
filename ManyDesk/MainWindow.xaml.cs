using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Desktop.Robot;
using Desktop.Robot.Clicks;
using Desktop.Robot.Extensions;
using HttpMultipartParser;
using LightHTTP;
using Newtonsoft.Json;
using ScreenCapturerNS;

namespace ManyDesk;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private static DateTime _time1 = DateTime.Now;
    private static DateTime _time2 = DateTime.Now;
    private static double _elapsedTime;
    
    private readonly LightHttpServer _server;
    private readonly Assembly _assembly;
    private readonly List<string> _sessions;
    private readonly string _password;
    private byte[]? _lastScreenCapture;
    private readonly JsonSerializer _serializer = new();
    private readonly Robot _robot = new();
    private readonly int _screenWidth = (int)SystemParameters.PrimaryScreenWidth;
    private readonly int _screenHeight = (int)SystemParameters.PrimaryScreenHeight;
    private long _quality;
    private int _resolutionX;
    private int _resolutionY;
    private int _refreshRate;
    
    [DllImport("user32.dll")]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);
    
    public MainWindow()
    {
        InitializeComponent();
        
        var vDevMode = new DEVMODE();
        var i = 0;
        while (EnumDisplaySettings(null!, i++, ref vDevMode))
        {
            if (ResolutionComboBox.Items.Cast<ComboBoxItem>()
                .Any(item =>
                    item.DataContext is Resolution resolution &&
                    resolution.Width == vDevMode.dmPelsWidth &&
                    resolution.Height == vDevMode.dmPelsHeight))
            {
                continue;
            }
            
            ResolutionComboBox.Items.Add(new ComboBoxItem
            {
                Content = $"{vDevMode.dmPelsWidth}x{vDevMode.dmPelsHeight}",
                DataContext = new Resolution(vDevMode.dmPelsWidth, vDevMode.dmPelsHeight)
            });
        }
        
        _quality = (long)QualitySlider.Value;
        _refreshRate = (int)FrameRateSlider.Value;
        _resolutionX = _screenWidth / 2;
        _resolutionY = _screenHeight / 2;
        
        _assembly = Assembly.GetExecutingAssembly();
        
        _sessions = new List<string>();
        _password =
            Environment.GetCommandLineArgs().FirstOrDefault(arg => arg.StartsWith("--password="))?.Split('=')[1] ??
            new Func<string>(() =>
            {
                Console.Error.WriteLine("Password not provided, supply with '--password=yourpassword'");
                Environment.Exit(1);
                return "";
            })();
        _server = new LightHttpServer();
        _server.Listener.Prefixes.Add("http://10.0.0.44:8753/");
        var th = new Thread(StartListen);
        th.Start();
    }

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        
        ScreenCapturer.OnScreenUpdated += OnScreenUpdated;
        ScreenCapturer.StartCapture();
    }
    
    //If you get 'dllimport unknown'-, then add 'using System.Runtime.InteropServices;'
    [DllImport("gdi32.dll", EntryPoint = "DeleteObject")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject([In] IntPtr hObject);

    private static ImageSource ImageSourceFromBitmap(Bitmap bmp)
    {
        var handle = bmp.GetHbitmap();
        try
        {
            return Imaging.CreateBitmapSourceFromHBitmap(handle, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        }
        finally { DeleteObject(handle); }
    }

    private void OnScreenUpdated(object? sender, OnScreenUpdatedEventArgs e)
    {
        _time2 = DateTime.Now;
        var deltaTime = (_time2.Ticks - _time1.Ticks) / 10000000f;
        _time1 = _time2;
        _elapsedTime += deltaTime;
        
        if (_elapsedTime < 1.0 / _refreshRate) return;
        _elapsedTime = 0;
        
        using (var mss = new MemoryStream())
        {
            e.Bitmap = new Bitmap(e.Bitmap, new System.Drawing.Size(_resolutionX, _resolutionY));
            var qualityParam = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, _quality);
            var imageCodec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            var parameters = new EncoderParameters(1);
            parameters.Param[0] = qualityParam;
            e.Bitmap.Save(mss, imageCodec, parameters);
            _lastScreenCapture = mss.ToArray();
        }
        
        Dispatcher.Invoke(() =>
        { 
            ScreenCapturePreview.Source = ImageSourceFromBitmap(e.Bitmap);
        });
    }
    
    private void StartListen()
    {
        Console.Out.WriteLine("Server started at: " + _server.Listener.Prefixes.First());
        
        _server.HandlesPath("/", async context =>
        {
            context.Response.ContentEncoding = Encoding.UTF8;
            context.Response.ContentType = "text/html";
            using var reader = new StreamReader(_assembly.GetManifestResourceStream("ManyDesk.index.html")!);
            var html = await reader.ReadToEndAsync();
            await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(html));
        });
        
        _server.HandlesPath("/login", async context =>
        {
            var parser = await MultipartFormDataParser.ParseAsync(context.Request.InputStream);
            
            if (parser.GetParameterValue("password") is not { } password || password != _password)
            {
                context.Response.StatusCode = 401;
                return;
            }
            
            var session = Guid.NewGuid().ToString();
            _sessions.Add(session);
            context.Response.StatusCode = 200;
            context.Response.Headers.Add("Set-Cookie", $"ManyDesk_Session={session}; Path=/");
        });

        _server.HandlesPath("/screen", context =>
        {
            if (context.Request.Headers["Cookie"] is not { } cookie || !_sessions.Contains(cookie.Split("ManyDesk_Session=")[1]))
            {
                context.Response.StatusCode = 401;
                return;
            }

            context.Response.ContentType = "blob";
            try
            {
                context.Response.OutputStream.Write(_lastScreenCapture);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
            }
        });
        
        _server.HandlesPath("/mousemove", context =>
        {
            if (context.Request.Headers["Cookie"] is not { } cookie || !_sessions.Contains(cookie.Split("ManyDesk_Session=")[1]))
            {
                context.Response.StatusCode = 401;
                return;
            }

            using var sr = new StreamReader(context.Request.InputStream);
            using var jsonTextReader = new JsonTextReader(sr);
            if (_serializer.Deserialize<Packets.MouseMove>(jsonTextReader) is not { } mouseMove)
            {
                context.Response.StatusCode = 400;
                return;
            }
            
            _robot.MouseMove(
                (int)(mouseMove.x * _screenWidth),
                (int)(mouseMove.y * _screenHeight));
        });
        
        _server.HandlesPath("/mousedown", context =>
        {
            if (context.Request.Headers["Cookie"] is not { } cookie || !_sessions.Contains(cookie.Split("ManyDesk_Session=")[1]))
            {
                context.Response.StatusCode = 401;
                return;
            }

            using var sr = new StreamReader(context.Request.InputStream);
            using var jsonTextReader = new JsonTextReader(sr);
            if (_serializer.Deserialize<Packets.MouseDown>(jsonTextReader) is not { } mouseDown)
            {
                context.Response.StatusCode = 400;
                return;
            }

            _robot.MouseDown(mouseDown.button switch
            {
                0 => Mouse.LeftButton(),
                1 => Mouse.MiddleButton(),
                2 => Mouse.RightButton()
            });
            
            context.Response.StatusCode = 200;
        });
        
        _server.HandlesPath("/mouseup", context =>
        {
            if (context.Request.Headers["Cookie"] is not { } cookie || !_sessions.Contains(cookie.Split("ManyDesk_Session=")[1]))
            {
                context.Response.StatusCode = 401;
                return;
            }

            using var sr = new StreamReader(context.Request.InputStream);
            using var jsonTextReader = new JsonTextReader(sr);
            if (_serializer.Deserialize<Packets.MouseUp>(jsonTextReader) is not { } mouseUp)
            {
                context.Response.StatusCode = 400;
                return;
            }

            _robot.MouseUp(mouseUp.button switch
            {
                0 => Mouse.LeftButton(),
                1 => Mouse.MiddleButton(),
                2 => Mouse.RightButton()
            });
            
            context.Response.StatusCode = 200;
        });
        
        _server.HandlesPath("/mousewheel", context =>
        {
            if (context.Request.Headers["Cookie"] is not { } cookie || !_sessions.Contains(cookie.Split("ManyDesk_Session=")[1]))
            {
                context.Response.StatusCode = 401;
                return;
            }

            using var sr = new StreamReader(context.Request.InputStream);
            using var jsonTextReader = new JsonTextReader(sr);
            if (_serializer.Deserialize<Packets.MouseWheel>(jsonTextReader) is not { } mouseWheel)
            {
                context.Response.StatusCode = 400;
                return;
            }

            _robot.MouseScroll(mouseWheel.delta);
            
            context.Response.StatusCode = 200;
        });
        
        _server.HandlesPath("/keydown", context =>
        {
            if (context.Request.Headers["Cookie"] is not { } cookie || !_sessions.Contains(cookie.Split("ManyDesk_Session=")[1]))
            {
                context.Response.StatusCode = 401;
                return;
            }

            using var sr = new StreamReader(context.Request.InputStream);
            using var jsonTextReader = new JsonTextReader(sr);
            if (_serializer.Deserialize<Packets.KeyDown>(jsonTextReader) is not { } keyDown || Enum.TryParse(keyDown.key.ToUpper(), out Key key))
            {
                context.Response.StatusCode = 400;
                return;
            }

            _robot.KeyDown(key);
            
            context.Response.StatusCode = 200;
        });
        
        _server.HandlesPath("/keyup", context =>
        {
            if (context.Request.Headers["Cookie"] is not { } cookie || !_sessions.Contains(cookie.Split("ManyDesk_Session=")[1]))
            {
                context.Response.StatusCode = 401;
                return;
            }

            using var sr = new StreamReader(context.Request.InputStream);
            using var jsonTextReader = new JsonTextReader(sr);
            if (_serializer.Deserialize<Packets.KeyUp>(jsonTextReader) is not { } keyUp || Enum.TryParse(keyUp.key.ToUpper(), out Key key))
            {
                context.Response.StatusCode = 400;
                return;
            }

            _robot.KeyUp(key);
            
            context.Response.StatusCode = 200;
        });
        
        _server.Start();
    }

    private void QualitySlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _quality = (long)e.NewValue;
    }

    private void ResolutionComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ResolutionComboBox.SelectedItem is ComboBoxItem { DataContext: Resolution resolution })
        {
            _resolutionX = resolution.Width;
            _resolutionY = resolution.Height;
        }
    }

    private void FrameRateSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _refreshRate = (int)e.NewValue;
    }
}