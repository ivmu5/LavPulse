#if WINDOWS
using Microsoft.Maui.Graphics;

namespace Laveyana.LavPulse.Controls;

/// <summary>Actual progress and decorative motion are separate. The moving
/// highlight never changes the reported percentage.</summary>
internal sealed class NotificationProgressDrawable(Func<Color> color, bool ring) : IDrawable
{
    internal double Value { get; set; }
    internal double Motion { get; set; }
    internal bool Active { get; set; }

    public void Draw(ICanvas canvas, RectF rect)
    {
        float w=rect.Width,h=rect.Height;
        if(w<=0||h<=0||!Active)return;
        Color accent=color()??Colors.Gray;
        float value=(float)Math.Clamp(Value,0,1);
        if(ring)
        {
            float x=rect.X+w/2,y=rect.Y+h/2,r=Math.Max(1,Math.Min(w,h)/2-3);
            canvas.StrokeSize=3;
            canvas.StrokeColor=accent.WithAlpha(.18f);
            canvas.DrawCircle(x,y,r);
            const int segments=96;
            int end=(int)Math.Ceiling(value*segments);
            for(int i=0;i<end;i++)
            {
                double a=-Math.PI/2+Math.Tau*i/segments;
                double b=-Math.PI/2+Math.Tau*Math.Min(value,(i+1d)/segments);
                double d=Math.Abs(i/(double)segments-Motion);
                canvas.StrokeColor=accent.WithAlpha((float)(.76+.24*Math.Max(0,1-Math.Min(d,1-d)*12)));
                canvas.DrawLine(x+r*(float)Math.Cos(a),y+r*(float)Math.Sin(a),
                    x+r*(float)Math.Cos(b),y+r*(float)Math.Sin(b));
            }
            return;
        }
        float radius=h/2;
        canvas.FillColor=accent.WithAlpha(.18f);
        canvas.FillRoundedRectangle(rect.X,rect.Y,w,h,radius);
        float filled=w*value;
        if(filled<=0)return;
        canvas.FillColor=accent;
        canvas.FillRoundedRectangle(rect.X,rect.Y,filled,h,Math.Min(radius,filled/2));
        // Soft highlight travels within the already completed fraction.
        float center=filled*(float)Motion;
        for(int i=-4;i<=4;i++)
        {
            float x=Math.Clamp(center+i*3,0,filled);
            float width=Math.Min(3,filled-x);
            if(width<=0)continue;
            canvas.FillColor=Colors.White.WithAlpha(.23f*(1-Math.Abs(i)/5f));
            canvas.FillRectangle(rect.X+x,rect.Y+h*.2f,width,h*.6f);
        }
    }
}
#endif
