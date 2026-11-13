namespace VowenLite;

// Pure double-tap detector for one key (Right Ctrl: opens the typing box). Fed key events with timestamps, so it can be tested
// without a keyboard. A tap = down then up within MaxTapMs with no other key in between; a double-tap = the second tap's down
// within MaxGapMs of the first tap's up. Fires on the second tap's up. Auto-repeat downs and any other key reset it.
sealed class TapDetector
{
    public const int MaxTapMs = 250, MaxGapMs = 350;
    readonly int vk;
    bool isDown, dirty;
    long downAt, lastTapUp = -1;

    public TapDetector(int vk) { this.vk = vk; }

    public void Down(int key, long tick)
    {
        if (key != vk) { Other(); return; }
        if (isDown) return;   // auto-repeat while held
        isDown = true; dirty = false; downAt = tick;
        if (lastTapUp >= 0 && tick - lastTapUp > MaxGapMs) lastTapUp = -1;
    }

    // True when this release completed a double-tap.
    public bool Up(int key, long tick)
    {
        if (key != vk || !isDown) return false;
        isDown = false;
        if (dirty || tick - downAt > MaxTapMs) { lastTapUp = -1; return false; }
        if (lastTapUp >= 0 && downAt - lastTapUp <= MaxGapMs) { lastTapUp = -1; return true; }
        lastTapUp = tick;
        return false;
    }

    // Any other key (or a mouse button): the press is not a clean tap.
    public void Other()
    {
        if (isDown) dirty = true;
        lastTapUp = -1;
    }

    // `--taptest`
    public static int Test()
    {
        int bad = 0;
        void Check(string what, bool ok) { Console.WriteLine((ok ? "ok   " : "FAIL ") + what); if (!ok) bad++; }
        const int K = 0xA3;
        bool Tap(TapDetector d, long t, int hold = 80) { d.Down(K, t); return d.Up(K, t + hold); }
        var d = new TapDetector(K);
        Check("double tap fires", !Tap(d, 0) && Tap(d, 200));
        d = new TapDetector(K);
        Check("slow second tap does not fire", !Tap(d, 0) && !Tap(d, 80 + MaxGapMs + 100));
        d = new TapDetector(K);
        Check("long hold is not a tap", !Tap(d, 0, 600) && !Tap(d, 700));
        d = new TapDetector(K);
        Check("long second hold does not fire", !Tap(d, 0) && !Tap(d, 200, 500));
        d = new TapDetector(K);
        d.Down(K, 0); d.Other(); d.Up(K, 80);
        Check("other key during first tap resets", !Tap(d, 200));
        d = new TapDetector(K);
        Tap(d, 0); d.Down(0x43, 150); d.Up(0x43, 190);
        Check("other key between taps resets", !Tap(d, 250));
        d = new TapDetector(K);
        Tap(d, 0); d.Down(K, 200); d.Other();
        Check("other key during second tap does not fire", !d.Up(K, 260));
        d = new TapDetector(K);
        d.Down(K, 0); d.Down(K, 40); d.Down(K, 80); d.Down(K, 120); d.Up(K, 160);
        Check("auto-repeat counts as one tap", !d.Up(K, 170) && Tap(d, 300));
        d = new TapDetector(K);
        d.Down(K, 0); for (int t = 30; t < 600; t += 30) d.Down(K, t); d.Up(K, 600);
        Check("held with auto-repeat then tap is no double", !Tap(d, 700));
        d = new TapDetector(K);
        Check("triple tap fires once", !Tap(d, 0) && Tap(d, 200) && !Tap(d, 400));
        d = new TapDetector(K);
        Tap(d, 0); Tap(d, 200);
        Check("fourth tap pairs with third", !Tap(d, 400) && Tap(d, 600));
        d = new TapDetector(K);
        Check("up without down ignored", !d.Up(K, 10));
        Console.WriteLine(bad == 0 ? "ALL PASS" : bad + " FAILED");
        return bad == 0 ? 0 : 1;
    }
}
