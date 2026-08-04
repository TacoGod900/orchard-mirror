namespace Orchard.Mirror.Agent.Windows;

/// <summary>Resolution-independent CoreDevice touchscreen coordinates.</summary>
public readonly record struct TouchCoordinates(int X, int Y)
{
    /// <summary>Map a point in a client rectangle onto the full unsigned 16-bit HID range.</summary>
    public static TouchCoordinates FromClient(int x, int y, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        return new TouchCoordinates(
            Normalize(x, width),
            Normalize(y, height));
    }

    private static int Normalize(int coordinate, int length)
    {
        if (length == 1)
        {
            return 0;
        }

        int clamped = Math.Clamp(coordinate, 0, length - 1);
        return checked((int)Math.Round(clamped * 65535.0 / (length - 1), MidpointRounding.AwayFromZero));
    }
}
