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
}