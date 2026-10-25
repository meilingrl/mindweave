using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Media;

namespace Mindweave.Desktop;

internal static class Icons
{
    public static Path Make(string name, string color = "#74786E", double size = 17) => new()
    {
        Width = size, Height = size, Stretch = Stretch.Uniform, Stroke = Brush.Parse(color), StrokeThickness = 1.55,
        StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
        Data = Geometry.Parse(name switch
        {
            "sidebar" => "M3,3 L17,3 L17,17 L3,17 Z M8,3 L8,17",
            "new" => "M12,3 L4,3 Q2,3 2,5 L2,16 Q2,18 4,18 L15,18 Q17,18 17,16 L17,10 M9,11 L10,7 L16,1 L19,4 L13,10 Z",
            "chat" => "M4,3 L16,3 Q18,3 18,5 L18,13 Q18,15 16,15 L8,15 L3,19 L3,5 Q3,3 4,3 Z",
            "branch" => "M5,2 L5,14 Q5,17 8,17 L17,17 M5,7 L17,7 M13,3 L17,7 L13,11",
            "arrow" => "M10,17 L10,3 M4,9 L10,3 L16,9",
            "stop" => "M5,5 L15,5 L15,15 L5,15 Z",
            "back" => "M8,4 L2,10 L8,16 M2,10 L13,10 Q18,10 18,5",
            "more" => "M3,10 L3.1,10 M10,10 L10.1,10 M17,10 L17.1,10",
            "chevron" => "M6,3 L12,9 L6,15",
            "copy" => "M7,2 L17,2 L17,12 M3,6 L13,6 L13,18 L3,18 Z",
            "check" => "M3,10 L8,15 L17,5",
            "close" => "M4,4 L16,16 M16,4 L4,16",
            "info" => "M10,2 A8,8 0 1 1 9.99,2 M10,9 L10,14 M10,6 L10.01,6",
            _ => "M3,3 L17,17 M17,3 L3,17"
        })
    };
}
