using Foundation;

namespace ByteBench.Desktop;

/// <summary>Xcode 27 and later require the UIKit scene lifecycle; MAUI's own scene delegate does the work.</summary>
[Register("SceneDelegate")]
public class SceneDelegate : MauiUISceneDelegate
{
}
