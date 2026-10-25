using Avalonia;

// Temporary prototype: bounded spring displacement, separate from saved layout positions.
sealed class LocalMotion
{
    public Vector Offset { get; private set; }
    Vector velocity;
    public bool Step(Vector drag, double strength)
    {
        if(strength<=0){Reset();return false;}
        var target=drag*(.35*Math.Clamp(strength,0,1));
        if(target.Length>48) target=target/target.Length*48;
        velocity=(velocity+(target-Offset)*.09)*.68;
        Offset+=velocity;
        if(Offset.Length>48) {Offset=Offset/Offset.Length*48;velocity=default;}
        if((target-Offset).Length<.02 && velocity.Length<.02){Offset=target;velocity=default;return false;}
        return true;
    }
    public void Reset() { Offset = default; velocity=default; }
}
static class MotionChecks
{
    public static void Run()
    {
        static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        var motion = new LocalMotion();
        for (int i=0;i<60;i++) motion.Step(new Vector(300,100), 0);
        Check(motion.Offset.Length == 0, "Zero strength must disable motion");
        for (int i=0;i<180;i++) motion.Step(new Vector(10000,10000), 1);
        Check(motion.Offset.Length > 1, "Neighbor should respond to dragging");
        Check(motion.Offset.Length <= 48.001, "Displacement must stay bounded");
        bool active=true; int ticks=0;
        while(active && ticks++<600) active=motion.Step(default,1);
        Check(!active && motion.Offset.Length == 0, "Released neighbor must return and sleep");
        for(int i=0;i<30;i++)motion.Step(new Vector(100,0),1);
        motion.Step(default,0);
        Check(motion.Offset.Length==0,"Turning strength off must clear existing motion");
        var low=new LocalMotion();var high=new LocalMotion();
        for(int i=0;i<120;i++){low.Step(new Vector(100,0),.2);high.Step(new Vector(100,0),.8);}
        Check(high.Offset.Length>low.Offset.Length,"Slider must change movement strength");
        var hover=new HoverMotion();
        for(int i=0;i<120;i++)hover.Step(new Vector(40,0),1);
        Check(hover.Offset.X>0 && hover.Offset.Length<=5,"Hover should attract within five screen units");
        for(int i=0;i<120;i++)hover.Step(new Vector(200,0),1);
        Check(hover.Offset.Length==0,"Distant pointer must not affect node");
        for(int i=0;i<120;i++)hover.Step(new Vector(40,0),1);
        bool moving=true;int frames=0;while(moving&&frames++<600)moving=hover.Step(null,1);
        Check(!moving&&hover.Offset.Length==0,"Hover exit must settle and sleep");
        for(int i=0;i<60;i++)hover.Step(new Vector(40,0),0);
        Check(hover.Offset.Length==0,"Zero strength disables hover motion");
        Console.WriteLine("PASS: hover attraction/bounds/range/exit/disable;  zero strength, response, bounds, settle/sleep, disable, strength ordering");
    }
}

sealed class HoverMotion
{
 public Vector Offset {get;private set;}
 public bool Step(Vector? cursorDelta,double strength)
 {
  if(strength<=0){Reset();return false;}
  Vector target=default;
  if(cursorDelta is Vector d && d.Length>0 && d.Length<90)
   target=d/d.Length*(5*Math.Clamp(strength,0,1)*Math.Sin(Math.PI*d.Length/90));
  Offset+=(target-Offset)*.18;
  if((target-Offset).Length<.01){Offset=target;return false;}
  return true;
 }
 public void Reset()=>Offset=default;
}
