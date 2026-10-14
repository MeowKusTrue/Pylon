using System.Windows;
using LibVLCSharp.Shared;

namespace Pylon;

/// <summary>
/// Yaw / pitch / field of view of a 360° video as dependency properties, so the spring engine can glide them:
/// a flick projects the view forward the way iOS scrolling does (WWDC 2018, "Designing Fluid Interfaces").
/// </summary>
public class ViewAngles : DependencyObject
{
    public static readonly DependencyProperty YawProperty = DependencyProperty.Register(nameof(Yaw), typeof(double), typeof(ViewAngles), new(0.0, Changed));
    public static readonly DependencyProperty PitchProperty = DependencyProperty.Register(nameof(Pitch), typeof(double), typeof(ViewAngles), new(0.0, Changed));
    public static readonly DependencyProperty FovProperty = DependencyProperty.Register(nameof(Fov), typeof(double), typeof(ViewAngles), new(80.0, Changed));

    public double Yaw { get => (double)GetValue(YawProperty); set => SetValue(YawProperty, value); }
    public double Pitch { get => (double)GetValue(PitchProperty); set => SetValue(PitchProperty, value); }
    public double Fov { get => (double)GetValue(FovProperty); set => SetValue(FovProperty, value); }

    MediaPlayer? _player;

    public void Attach(MediaPlayer player)
    {
        _player = player;
        Apply();
    }

    static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((ViewAngles)d).Apply();

    void Apply() => _player?.UpdateViewpoint((float)Yaw, (float)Pitch, 0, (float)Fov, true);

    /// <summary>Stops a glide so the view follows the pointer from where it is.</summary>
    public void Hold()
    {
        Motion.Set(this, YawProperty, Yaw);
        Motion.Set(this, PitchProperty, Pitch);
    }

    public void Move(double yaw, double pitch)
    {
        Yaw += yaw;
        Pitch = Math.Clamp(Pitch + pitch, -90, 90);
    }

    /// <summary>Lets go with a velocity (degrees/s): the view decelerates to where the flick projects it.</summary>
    public void Fling(double vYaw, double vPitch)
    {
        const double rate = 0.998; // Apple's normal scroll deceleration
        static double Project(double v) => v / 1000 * rate / (1 - rate);
        Motion.Animate(this, YawProperty, Yaw + Project(vYaw), Spring.Smooth);
        Motion.Animate(this, PitchProperty, Math.Clamp(Pitch + Project(vPitch), -90, 90), Spring.Smooth);
    }

    public void Zoom(double delta) => Motion.Animate(this, FovProperty, Math.Clamp(Fov + delta, 30, 140), Spring.Snappy);

    public void Reset(bool animate = true)
    {
        if (animate)
        {
            Motion.Animate(this, YawProperty, 0, Spring.Smooth);
            Motion.Animate(this, PitchProperty, 0, Spring.Smooth);
            Motion.Animate(this, FovProperty, 80, Spring.Smooth);
        }
        else
        {
            Motion.Set(this, YawProperty, 0);
            Motion.Set(this, PitchProperty, 0);
            Motion.Set(this, FovProperty, 80);
        }
    }
}
