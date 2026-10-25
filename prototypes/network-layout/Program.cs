using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using System.Globalization;
class App:Application {
 public override void Initialize(){Styles.Add(new FluentTheme());RequestedThemeVariant=Avalonia.Styling.ThemeVariant.Dark;}
 public override void OnFrameworkInitializationCompleted(){if(ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d)d.MainWindow=new Main();base.OnFrameworkInitializationCompleted();}
}
class Program{[STAThread]public static void Main(string[] args){if(args.Contains("--check-motion")){MotionChecks.Run();return;}AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().StartWithClassicDesktopLifetime(args);}}
record Node(int Id,string Title,int? Parent,Point P,bool Indirect=false){public bool Read;}
class Map:Control {
 public List<Node> Nodes=new();public int? Selected;public string Query="";public Action<Node,bool>? Pick;
 double scale=.8;Vector offset=new(80,100);Point down,last;bool dragging;int? pressed,hovered;
 public bool WheelZoom;
 readonly Dictionary<int,LocalMotion> motions=new();
 readonly Dictionary<int,HoverMotion> hoverMotions=new();
 Point? pointer; bool pointerDown,hoverEnabled=true;
 public bool HoverEnabled {get=>hoverEnabled;set{hoverEnabled=value;physics.Start();}}
 Point NodeScreen(Node n)=>Screen(Position(n))+(hoverMotions.TryGetValue(n.Id,out var h)?h.Offset:default);
 void ClearHover(){pointer=null;hovered=null;physics.Start();}

 readonly DispatcherTimer physics=new(){Interval=TimeSpan.FromMilliseconds(16)};
 Point dragStart; Vector dragDelta;
 double strength=.3;
 public double Strength {get=>strength;set{strength=Math.Clamp(value,0,1);if(strength==0){foreach(var m in motions.Values)m.Reset();foreach(var h in hoverMotions.Values)h.Reset();physics.Stop();InvalidateVisual();}else physics.Start();}}
 Point Position(Node n)=>n.P+(motions.TryGetValue(n.Id,out var m)?m.Offset:default);
 void Tick(){bool active=false;foreach(var n in Nodes){if(!motions.TryGetValue(n.Id,out var m))motions[n.Id]=m=new();var source=dragging&&pressed is int id?Nodes.FirstOrDefault(x=>x.Id==id):null;bool adjacent=source!=null&&n.Id!=source.Id&&(n.Parent==source.Id||source.Parent==n.Id);active=m.Step(adjacent?dragDelta:default,strength)||active;
 if(!hoverMotions.TryGetValue(n.Id,out var h))hoverMotions[n.Id]=h=new();
 Vector? delta=hoverEnabled&&!pointerDown&&pointer is Point pt?pt-Screen(n.P):null;
 active=h.Step(delta,strength)||active;}InvalidateVisual();if(!active)physics.Stop();}
 void FinishDrag(){pointerDown=false;pressed=null;dragging=false;dragDelta=default;physics.Start();}
 protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e){physics.Stop();base.OnDetachedFromVisualTree(e);}
 protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e){base.OnPointerCaptureLost(e);FinishDrag();}

 public void Zoom(double factor,Point? anchor=null){ClearHover();var p=anchor??new Point(Bounds.Width/2,Bounds.Height/2);double old=scale;scale=Math.Clamp(scale*factor,.2,2);offset=new(p.X-(p.X-offset.X)*scale/old,p.Y-(p.Y-offset.Y)*scale/old);InvalidateVisual();}
 HashSet<int> Trail(){var ids=new HashSet<int>();int? id=Selected;while(id is int v&&ids.Add(v))id=Nodes.First(n=>n.Id==v).Parent;return ids;}
 static IBrush Ink(string s)=>Brush.Parse(s);
 static FormattedText Text(string s,double size,IBrush b)=>new(s,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface("Inter"),size,b);
 public Map(){ClipToBounds=true;physics.Tick+=(_,_)=>Tick();Reset();}
 public void Reset(){physics.Stop();motions.Clear();hoverMotions.Clear();pointer=null;pointerDown=false;pressed=null;dragging=false;dragDelta=default;Nodes.Clear();Selected=null;hovered=null;string[] titles=["火车历史","蒸汽机－原理","热效率","冷凝器","铁路与城市","工业革命","电力牵引","摄影入门","光圈","景深","快门","感光度"];int?[] parents=[null,0,1,1,0,0,0,null,7,8,7,7];for(int i=0;i<titles.Length;i++){Point p;if(parents[i]==null)p=new(i==0?220:1150,350);else{var parent=Nodes.First(n=>n.Id==parents[i]);double a=(i*137.508)*Math.PI/180;p=new(parent.P.X+Math.Cos(a)*220,parent.P.Y+Math.Sin(a)*220);p=Place(parent.P,titles[i],i,p);}Nodes.Add(new(i,titles[i],parents[i],p,i==6));}InvalidateVisual();}
 Rect Box(Point p,string title)=>new(p.X-18,p.Y-30,Math.Max(65,title.Length*16+45),65);
 Point Place(Point parent,string title,int seed,Point initial){if(Nodes.All(n=>!Box(n.P,n.Title).Intersects(Box(initial,title))))return initial;for(int ring=1;ring<40;ring++)for(int i=0;i<24;i++){double a=(i*137.508+seed*23)*Math.PI/180;var p=new Point(parent.X+Math.Cos(a)*ring*155,parent.Y+Math.Sin(a)*ring*155);if(Nodes.All(n=>!Box(n.P,n.Title).Intersects(Box(p,title))))return p;}throw new InvalidOperationException("No free position");}
 public void Add(){var p=Nodes.First(n=>n.Id==(Selected??1));int id=Nodes.Max(n=>n.Id)+1;string t=id%3==0?"蒸汽机－市场":id%3==1?"蒸汽机－局限":"活塞与连杆";var pos=Place(p.P,t,id,new Point(p.P.X+200,p.P.Y));Nodes.Add(new(id,t,p.Id,pos));Selected=id;Pick?.Invoke(Nodes[^1],false);InvalidateVisual();}
 Point Screen(Point p)=>new(p.X*scale+offset.X,p.Y*scale+offset.Y);
 int? Hit(Point p){foreach(var n in Nodes.AsEnumerable().Reverse()){var q=NodeScreen(n);var original=Screen(n.P);if(new Rect(q.X-16,q.Y-20,n.Title.Length*14+40,40).Contains(p)||new Rect(original.X-16,original.Y-20,n.Title.Length*14+40,40).Contains(p))return n.Id;}return null;}
 public void Fit(){ClearHover();if(Bounds.Width<1)return;double x=Nodes.Min(n=>n.P.X)-40,y=Nodes.Min(n=>n.P.Y)-60,w=Nodes.Max(n=>n.P.X+n.Title.Length*16+40)-x,h=Nodes.Max(n=>n.P.Y+50)-y;scale=Math.Clamp(Math.Min((Bounds.Width-90)/w,(Bounds.Height-90)/h),.25,1.3);offset=new((Bounds.Width-w*scale)/2-x*scale,(Bounds.Height-h*scale)/2-y*scale);InvalidateVisual();}
 public void FocusMatch(){ClearHover();var n=Nodes.FirstOrDefault(n=>n.Title.Contains(Query,StringComparison.OrdinalIgnoreCase));if(n==null)return;Selected=n.Id;offset=new(Bounds.Width/2-n.P.X*scale,Bounds.Height/2-n.P.Y*scale);Pick?.Invoke(n,false);InvalidateVisual();}
 public override void Render(DrawingContext c){base.Render(c);c.FillRectangle(Ink("#18191b"),new Rect(Bounds.Size));for(double x=offset.X%28;x<Bounds.Width;x+=28)for(double y=offset.Y%28;y<Bounds.Height;y+=28)c.DrawEllipse(Ink("#292b30"),null,new Point(x,y),.8,.8);
 var trail=Trail();
 foreach(var n in Nodes.Where(n=>n.Parent!=null)){bool active=trail.Contains(n.Id),near=hovered==n.Id||hovered==n.Parent;c.DrawLine(new Pen(Ink(active?"#aab8f5":near?"#7f899e":n.Indirect?"#626577":"#363a42"),active?2.3:near?1.8:1.2,n.Indirect?DashStyle.Dash:null),NodeScreen(Nodes.First(p=>p.Id==n.Parent)),NodeScreen(n));}
 foreach(var n in Nodes){var p=NodeScreen(n);bool selected=Selected==n.Id,hover=hovered==n.Id,match=Query.Length>0&&n.Title.Contains(Query,StringComparison.OrdinalIgnoreCase);double r=(n.Parent==null?10:7)+(hover?2:0);
 if(hover||selected)c.DrawEllipse(Ink(selected?"#26324d":"#282e3c"),null,p,r+11,r+11);
 if(selected||match)c.DrawEllipse(null,new Pen(Ink(selected?"#c1c6fa":"#dfb87c"),2),p,r+5,r+5);
 c.DrawEllipse(Ink(hover?"#e2e8ff":trail.Contains(n.Id)?"#bdc9f2":n.Read?"#747b88":"#b4bacd"),null,p,r,r);
 if(!n.Read)c.DrawEllipse(Ink("#a3b8ee"),null,new Point(p.X+r+3,p.Y-r-3),2.5,2.5);
 if(scale>.45||selected||match||hover)c.DrawText(Text(n.Title,13*Math.Min(1,scale/.65),Ink(hover||selected?"#f0f2ff":"#cbd0da")),new Point(p.X+r+9,p.Y-9));
 if(n.Parent==null)c.DrawText(Text(n.Id==0?"探索 01 · 交通与工业":"探索 02 · 光与影",11,Ink("#858b97")),new Point(p.X-25,p.Y-42));}}
 protected override void OnPointerExited(PointerEventArgs e){base.OnPointerExited(e);ClearHover();InvalidateVisual();}
 protected override void OnPointerPressed(PointerPressedEventArgs e){base.OnPointerPressed(e);if(!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)return;pointerDown=true;down=last=e.GetPosition(this);pressed=Hit(down);dragging=false;dragDelta=default;if(pressed is int id){var n=Nodes.First(n=>n.Id==id);dragStart=Position(n);if(motions.TryGetValue(id,out var m))m.Reset();}foreach(var h in hoverMotions.Values)h.Reset();physics.Start();e.Pointer.Capture(this);}
 protected override void OnPointerMoved(PointerEventArgs e){base.OnPointerMoved(e);var p=e.GetPosition(this);pointer=p;physics.Start();var hit=Hit(p);if(hovered!=hit){hovered=hit;Cursor=new Cursor(hit!=null?StandardCursorType.Hand:StandardCursorType.Arrow);InvalidateVisual();}if(e.Pointer.Captured!=this)return;if(Math.Sqrt(Math.Pow(p.X-down.X,2)+Math.Pow(p.Y-down.Y,2))>5)dragging=true;if(dragging){if(pressed is int id){var n=Nodes.First(n=>n.Id==id);dragDelta=(p-down)/scale;Nodes[Nodes.IndexOf(n)]=n with {P=dragStart+dragDelta};Selected=id;physics.Start();}else offset+=p-last;InvalidateVisual();}last=p;}
 protected override void OnPointerReleased(PointerReleasedEventArgs e){base.OnPointerReleased(e);if(e.Pointer.Captured!=this)return;if(pressed is int id){Selected=id;Pick?.Invoke(Nodes.First(n=>n.Id==id),false);InvalidateVisual();}FinishDrag();e.Pointer.Capture(null);}
 protected override void OnDoubleTapped(TappedEventArgs e){base.OnDoubleTapped(e);var id=Hit(e.GetPosition(this));if(id!=null){Selected=id;Pick?.Invoke(Nodes.First(n=>n.Id==id),true);}}
 protected override void OnPointerWheelChanged(PointerWheelEventArgs e){base.OnPointerWheelChanged(e);ClearHover();if(WheelZoom||(e.KeyModifiers&(KeyModifiers.Control|KeyModifiers.Meta))!=0)Zoom(Math.Exp(Math.Clamp(e.Delta.Y*.08,-.3,.3)),e.GetPosition(this));else{offset+=new Vector(e.Delta.X*35,e.Delta.Y*35);InvalidateVisual();}e.Handled=true;}

}
class Main:Window {
 readonly Map map=new();readonly TextBlock heading=new(){Text="探索网络",FontSize=22};readonly TextBlock info=new(){Text="单击节点查看来源。\n打开正文才会标记已读。",TextWrapping=TextWrapping.Wrap};readonly Border detail=new(){IsVisible=false,Padding=new Thickness(38),Background=Brush.Parse("#1d1e22")};
 Button Btn(string text,Action a){var b=new Button{Content=text};b.Click+=(_,_)=>a();return b;}
 public Main(){Title="Mindweave · 网络布局交互样例";Width=1320;Height=840;MinWidth=900;MinHeight=580;Background=Brush.Parse("#18191b");var root=new Grid{RowDefinitions=new("Auto,*,Auto"),Margin=new Thickness(18)};var top=new StackPanel{Orientation=Avalonia.Layout.Orientation.Horizontal,Spacing=12,Margin=new Thickness(8,8,8,18)};top.Children.Add(new TextBlock{Text="mindweave  /  探索网络",FontSize=17,VerticalAlignment=Avalonia.Layout.VerticalAlignment.Center});var search=new TextBox{PlaceholderText="查找知识点…",Width=190};search.TextChanged+=(_,_)=>{map.Query=search.Text??"";map.InvalidateVisual();};search.KeyDown+=(_,e)=>{if(e.Key==Key.Enter)map.FocusMatch();};top.Children.Add(search);top.Children.Add(Btn("显示全部",()=>map.Fit()));top.Children.Add(Btn("新增关联节点",()=>map.Add()));top.Children.Add(Btn("重置样例",()=>{map.Reset();map.Fit();detail.IsVisible=false;heading.Text="探索网络";info.Text="单击节点查看来源。\n打开正文才会标记已读。";}));root.Children.Add(top);
 var body=new Grid{ColumnDefinitions=new("*,260")};Grid.SetRow(body,1);var host=new Grid();host.Children.Add(map);host.Children.Add(detail);body.Children.Add(host);var side=new StackPanel{Spacing=16,Margin=new Thickness(24,20,8,8)};var zoom=new StackPanel{Orientation=Avalonia.Layout.Orientation.Horizontal,Spacing=8};zoom.Children.Add(Btn("−",()=>map.Zoom(1/1.2)));zoom.Children.Add(Btn("＋",()=>map.Zoom(1.2)));side.Children.Add(zoom);var wheel=new CheckBox{Content="鼠标滚轮直接缩放"};wheel.IsCheckedChanged+=(_,_)=>map.WheelZoom=wheel.IsChecked==true;side.Children.Add(wheel);var strengthLabel=new TextBlock{Text="交互强度 · 30%（保守）",FontSize=13};side.Children.Add(strengthLabel);var strengthSlider=new Slider{Minimum=0,Maximum=100,Value=30};strengthSlider.PropertyChanged+=(_,e)=>{if(e.Property==Slider.ValueProperty){map.Strength=strengthSlider.Value/100;strengthLabel.Text=$"交互强度 · {strengthSlider.Value:0}%"+(strengthSlider.Value==0?"（关闭运动）":"");}};side.Children.Add(strengthSlider);var hoverToggle=new CheckBox{Content="悬停轻微吸引",IsChecked=true};hoverToggle.IsCheckedChanged+=(_,_)=>map.HoverEnabled=hoverToggle.IsChecked==true;side.Children.Add(hoverToggle);side.Children.Add(heading);side.Children.Add(info);side.Children.Add(Btn("打开总结",()=>{if(map.Selected is int id)Open(map.Nodes.First(n=>n.Id==id));}));side.Children.Add(new TextBlock{Text="双指滚动平移 · 空白拖动平移\n⌘ / Ctrl + 滚动缩放 · ＋ / − 缩放\n回车聚焦搜索 · 双击打开\n虚线：经过未总结会话\n\n靠近圆点轻微吸引，离开回位。\n悬停开关不影响拖动牵引。\n选中高亮回溯来源路径。\n新增时保留旧节点位置。\n拖动节点，邻居轻微跟随。\n松手后邻居回稳；拖动位置保留。\n强度 0 关闭运动，不影响拖动。",TextWrapping=TextWrapping.Wrap,FontSize=12,Foreground=Brush.Parse("#9299a6")});var sideScroll=new ScrollViewer{Content=side};Grid.SetColumn(sideScroll,1);body.Children.Add(sideScroll);root.Children.Add(body);var footer=new TextBlock{Text="交互样例 · 固定内容 · 无 AI / 数据库 · 关闭不保留状态",FontSize=11,Foreground=Brush.Parse("#9299a6")};Grid.SetRow(footer,2);root.Children.Add(footer);Content=root;
 map.Pick=(n,open)=>{heading.Text=n.Title;info.Text=(n.Read?"已读":"未读")+" · 当前采用版本\n\n来源路径\n"+Path(n)+(n.Indirect?"\n\n经 1 个未总结会话：能源转型":"");if(open)Open(n);};Opened+=(_,_)=>map.Fit();}
 string Path(Node n)=>n.Parent is int id?Path(map.Nodes.First(x=>x.Id==id))+" → "+n.Title:n.Title;
 void Open(Node n){n.Read=true;map.InvalidateVisual();var panel=new StackPanel{Spacing=24};panel.Children.Add(Btn("← 返回网络",()=>{detail.IsVisible=false;map.Pick?.Invoke(n,false);}));panel.Children.Add(new TextBlock{Text=n.Title,FontSize=28});panel.Children.Add(new TextBlock{Text="演示总结 · 已读\n\n"+Path(n)+"\n\n这是固定示例正文。用于体验从节点进入总结，再返回原来的缩放、位置和选择。\n\n实际总结与来源会话将在正式版接入。",FontSize=16,TextWrapping=TextWrapping.Wrap});detail.Child=panel;detail.IsVisible=true;}
}
