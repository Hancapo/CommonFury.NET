using System.Numerics;

namespace CommonFury.Numerics;

/// <summary>Numerically robust 3x3 linear algebra for affine row-vector matrices.</summary>
public static class MatrixMath
{
    // One-sided Jacobi SVD of the 3x3 linear matrix: A+ = V * S+ * U^T. Double precision; unlike A^T*A,
    // this does not square the condition number.
    /// <summary>Moore-Penrose pseudoinverse of the upper-left 3x3 part; translation and projection are discarded.</summary>
    public static Matrix4x4 PseudoInverseLinear(Matrix4x4 m)
    {
        Span<double> a = stackalloc double[] { m.M11, m.M12, m.M13, m.M21, m.M22, m.M23, m.M31, m.M32, m.M33 };
        Span<double> v = stackalloc double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
        for (int sweep = 0; sweep < 32; sweep++)
        {
            bool changed = false;
            for (int p = 0; p < 2; p++)
                for (int q = p + 1; q < 3; q++)
                {
                    double alpha = 0, beta = 0, gamma = 0;
                    for (int row = 0; row < 3; row++)
                    {
                        double x = a[row * 3 + p], y = a[row * 3 + q];
                        alpha += x * x;
                        beta += y * y;
                        gamma += x * y;
                    }
                    if (Math.Abs(gamma) <= 1e-15 * Math.Sqrt(alpha * beta))
                        continue;
                    double zeta = (beta - alpha) / (2 * gamma);
                    double t = Math.CopySign(1, zeta) / (Math.Abs(zeta) + Math.Sqrt(1 + zeta * zeta));
                    double c = 1 / Math.Sqrt(1 + t * t), s = c * t;
                    RotateColumns(a, p, q, c, s);
                    RotateColumns(v, p, q, c, s);
                    changed = true;
                }
            if (!changed)
                break;
        }
        Span<double> squared = stackalloc double[3];
        squared.Clear();
        for (int col = 0; col < 3; col++)
            for (int row = 0; row < 3; row++)
                squared[col] += a[row * 3 + col] * a[row * 3 + col];
        double cutoff = Math.Max(squared[0], Math.Max(squared[1], squared[2])) * 1e-30;
        Span<double> result = stackalloc double[9];
        result.Clear();
        for (int row = 0; row < 3; row++)
            for (int col = 0; col < 3; col++)
                for (int k = 0; k < 3; k++)
                    if (squared[k] > cutoff)
                        result[row * 3 + col] += v[row * 3 + k] * a[col * 3 + k] / squared[k];
        return new((float)result[0], (float)result[1], (float)result[2], 0,
            (float)result[3], (float)result[4], (float)result[5], 0,
            (float)result[6], (float)result[7], (float)result[8], 0, 0, 0, 0, 1);
    }

    private static void RotateColumns(Span<double> matrix, int p, int q, double c, double s)
    {
        for (int row = 0; row < 3; row++)
        {
            double x = matrix[row * 3 + p], y = matrix[row * 3 + q];
            matrix[row * 3 + p] = c * x - s * y;
            matrix[row * 3 + q] = s * x + c * y;
        }
    }
}
