using System;

class T
{
    static float W = 1920f, H = 1080f;
    static float cx, cy, sw, sh, ang;

    static float[] U() { return new float[] { (float)Math.Cos(ang), (float)Math.Sin(ang) }; }
    static float[] V() { return new float[] { (float)-Math.Sin(ang), (float)Math.Cos(ang) }; }

    static float[][] Corners()
    {
        float[] u = U(), v = V();
        float hw = sw / 2f, hh = sh / 2f;
        return new float[][] {
            new float[] { cx - u[0]*hw - v[0]*hh, cy - u[1]*hw - v[1]*hh },
            new float[] { cx + u[0]*hw - v[0]*hh, cy + u[1]*hw - v[1]*hh },
            new float[] { cx + u[0]*hw + v[0]*hh, cy + u[1]*hw + v[1]*hh },
            new float[] { cx - u[0]*hw + v[0]*hh, cy - u[1]*hw + v[1]*hh }
        };
    }

    static float[] SelBounds()
    {
        float[][] cs = Corners();
        float minx = cs[0][0], maxx = cs[0][0], miny = cs[0][1], maxy = cs[0][1];
        for (int i = 1; i < 4; i++)
        {
            if (cs[i][0] < minx) minx = cs[i][0];
            if (cs[i][0] > maxx) maxx = cs[i][0];
            if (cs[i][1] < miny) miny = cs[i][1];
            if (cs[i][1] > maxy) maxy = cs[i][1];
        }
        return new float[] { minx, miny, maxx - minx, maxy - miny };
    }

    static void ResizeTo(float mouseX, float mouseY, int corner, float ratio)
    {
        float[] u = U(), v = V();
        float[][] cs = Corners();
        float[] fx = cs[(corner + 2) % 4];
        float su = (corner == 1 || corner == 2) ? 1f : -1f;
        float sv = (corner == 2 || corner == 3) ? 1f : -1f;
#if OLD
        su = 1f; sv = 1f;      // 旧代码：没有符号修正
#endif

        float mx = mouseX, my = mouseY;
        if (mx < 0f) mx = 0f; if (mx > W) mx = W;
        if (my < 0f) my = 0f; if (my > H) my = H;

        float dx = mx - fx[0], dy = my - fx[1];
        float du = (dx * u[0] + dy * u[1]) * su;
        float dv = (dx * v[0] + dy * v[1]) * sv;
        if (du < 6f) du = 6f;
        if (dv < 6f) dv = 6f;
        if (ratio > 0f) { if (du / dv > ratio) dv = du / ratio; else du = dv * ratio; }

        bool fxInside = fx[0] >= -0.5f && fx[0] <= W + 0.5f && fx[1] >= -0.5f && fx[1] <= H + 0.5f;
        if (fxInside)
        {
            float[] ea = { 0f, su, su, 0f };
            float[] eb = { 0f, 0f, sv, sv };
            float t = 1f;
            for (int k = 0; k < 4; k++)
            {
                float ex = ea[k]*du*u[0] + eb[k]*dv*v[0];
                float ey = ea[k]*du*u[1] + eb[k]*dv*v[1];
                if (ex > 0.001f) { float q = (W - fx[0]) / ex; if (q < t) t = q; }
                else if (ex < -0.001f) { float q = (0f - fx[0]) / ex; if (q < t) t = q; }
                if (ey > 0.001f) { float q = (H - fx[1]) / ey; if (q < t) t = q; }
                else if (ey < -0.001f) { float q = (0f - fx[1]) / ey; if (q < t) t = q; }
            }
            float tMin = Math.Max(6f / du, 6f / dv);
            if (t < tMin) t = tMin;
            if (t > 1f) t = 1f;
            du *= t; dv *= t;
        }

        sw = du; sh = dv;
        cx = fx[0] + u[0]*su*du/2f + v[0]*sv*dv/2f;
        cy = fx[1] + u[1]*su*du/2f + v[1]*sv*dv/2f;
    }

    static int failures = 0;

    static void Case(float angDeg, float startW, float startH, float startCx, float startCy, float ratio)
    {
        ang = angDeg * (float)Math.PI / 180f;
        for (int corner = 0; corner < 4; corner++)
        {
            cx = startCx; cy = startCy; sw = startW; sh = startH;
            float[][] c0 = Corners();
            float[] anchor = c0[(corner + 2) % 4];
            float[] grab = c0[corner];

            // 沿着“远离固定角”的方向拖 240px，再往回拖 240px，每步 4px
            float[] u = U(), v = V();
            float su = (corner == 1 || corner == 2) ? 1f : -1f;
            float sv = (corner == 2 || corner == 3) ? 1f : -1f;
            float dirX = u[0]*su + v[0]*sv, dirY = u[1]*su + v[1]*sv;
            float len = (float)Math.Sqrt(dirX*dirX + dirY*dirY);
            dirX /= len; dirY /= len;

            float pw = sw, ph = sh;
            float maxJump = 0f;
            bool first = true;
            for (int step = -60; step <= 60; step++)
            {
                float mx = grab[0] + dirX * step * 4f;
                float my = grab[1] + dirY * step * 4f;
                ResizeTo(mx, my, corner, ratio);

                float[] a2 = Corners()[(corner + 2) % 4];
                float d = (float)Math.Sqrt(Math.Pow(a2[0]-anchor[0],2) + Math.Pow(a2[1]-anchor[1],2));
                if (d > 0.6f)
                {
                    Console.WriteLine("  FAIL anchor moved corner={0} ang={1} step={2} by {3:F2}px", corner, angDeg, step, d);
                    failures++; break;
                }
                float jump = Math.Max(Math.Abs(sw - pw), Math.Abs(sh - ph));
                if (!first && jump > maxJump) maxJump = jump;
                bool wasFirst = first;
                first = false;
                pw = sw; ph = sh;

                if (sw < 5.9f || sh < 5.9f) { Console.WriteLine("  FAIL collapsed corner={0} ang={1}", corner, angDeg); failures++; break; }

                float[] bb = SelBounds();
                bool inside = bb[0] >= -0.6f && bb[1] >= -0.6f && bb[0]+bb[2] <= W+0.6f && bb[1]+bb[3] <= H+0.6f;
                if (anchor[0] >= -0.5f && anchor[0] <= W+0.5f && anchor[1] >= -0.5f && anchor[1] <= H+0.5f && !inside)
                {
                    Console.WriteLine("  FAIL offscreen corner={0} ang={1} bbox=({2:F0},{3:F0},{4:F0}x{5:F0})", corner, angDeg, bb[0], bb[1], bb[2], bb[3]);
                    failures++; break;
                }
                if (!wasFirst && jump > 40f)
                {
                    Console.WriteLine("  FAIL jump corner={0} ang={1} step={2} jump={3:F1}px", corner, angDeg, step, jump);
                    failures++; break;
                }
            }
            Console.WriteLine("  corner {0} ang {1,6:F0}  start {2:F0}x{3:F0} -> max step jump {4:F2}px  OK", corner, angDeg, startW, startH, maxJump);
        }
    }

    static void Main()
    {
        float[] angs = { 0f, 7f, 15f, 30f, 45f, 60f, 73f, 90f, 118f, 137f, 180f, 215f, 270f, -37f, -90f, -128f };
        float[] ratios = { 0f, 1f, 16f/9f, 9f/16f };
        foreach (float ratio in ratios)
        {
            Console.WriteLine("=== ratio {0} ===", ratio == 0f ? "自由" : ratio.ToString("F2"));
            foreach (float a in angs)
            {
                Case(a, 400f, 300f, 960f, 540f, ratio);          // 屏幕中间
                Case(a, 900f, 700f, 300f, 250f, ratio);          // 靠左上，容易出界
                Case(a, 300f, 200f, 1750f, 950f, ratio);         // 靠右下
            }
        }
        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL PASS" : ("FAILURES: " + failures));
    }
}
