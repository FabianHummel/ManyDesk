using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
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
    private readonly LightHttpServer _server;
    private readonly Assembly _assembly;
    private readonly List<string> _sessions;
    private readonly string _password;
    private byte[]? _lastScreenCapture;
    private readonly ImageConverter _converter = new();
    private readonly JsonSerializer _serializer = new();
    private readonly Robot _robot = new();
    private int _screenWidth = (int)SystemParameters.PrimaryScreenWidth;
    private int _screenHeight = (int)SystemParameters.PrimaryScreenHeight;
    
    public MainWindow()
    {
        InitializeComponent();
        
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
        _server.Listener.Prefixes.Add("http://localhost:8080/");
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
        _lastScreenCapture = (byte[])_converter.ConvertTo(e.Bitmap, typeof(byte[]))!;
        
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
            context.Response.Headers.Add("Set-Cookie", $"session={session}; Path=/");
        });

        _server.HandlesPath("/screen", context =>
        {
            if (context.Request.Headers["Cookie"] is not { } cookie || !_sessions.Contains(cookie.Split('=')[1]))
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
            if (context.Request.Headers["Cookie"] is not { } cookie || !_sessions.Contains(cookie.Split('=')[1]))
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
            if (context.Request.Headers["Cookie"] is not { } cookie || !_sessions.Contains(cookie.Split('=')[1]))
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
                1 => Mouse.LeftButton(),
                2 => Mouse.MiddleButton(),
                3 => Mouse.RightButton()
            });
        });
        
        _server.HandlesPath("/mouseup", context =>
        {
            if (context.Request.Headers["Cookie"] is not { } cookie || !_sessions.Contains(cookie.Split('=')[1]))
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
                1 => Mouse.LeftButton(),
                2 => Mouse.MiddleButton(),
                3 => Mouse.RightButton()
            });
        });
        
        _server.Start();
    }
}