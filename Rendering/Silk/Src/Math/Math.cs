namespace Mainframe;

/// <summary>
/// Math helpers
/// </summary>
public static class Math
{
    public static double DegreesToRadians(double degrees)
    {
        return System.Math.PI / 180 * degrees;
    }
    
    public static float DegreesToRadiansF(float degrees)
    {
        return (float)DegreesToRadians(degrees);
    }
}