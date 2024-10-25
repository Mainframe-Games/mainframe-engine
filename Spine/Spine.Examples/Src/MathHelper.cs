namespace SilkSpine;

public static class MathHelper
{
    public static double DegreesToRadians(double degrees)
    {
        return Math.PI / 180 * degrees;
    }
    
    public static float DegreesToRadiansF(float degrees)
    {
        return (float)DegreesToRadians(degrees);
    }
}