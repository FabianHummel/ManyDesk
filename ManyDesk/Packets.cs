namespace ManyDesk;

public class Packets
{
    public class MouseMove
    {
        public float x { get; set; }
        public float y { get; set; }
    }
    
    public class MouseDown
    {
        public int button { get; set; }
    }
    
    public class MouseUp
    {
        public int button { get; set; }
    }
    
    public class MouseWheel
    {
        public int delta { get; set; }
    }
    
    public class KeyDown
    {
        public string key { get; set; }
    }
    
    public class KeyUp
    {
        public string key { get; set; }
    }
}