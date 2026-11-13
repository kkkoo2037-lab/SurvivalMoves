using System;
using System.Runtime.InteropServices;
using GTA;
using GTA.Math;
using GTA.Native;

// SurvivalMoves - ScriptHookVDotNet v3
// L3 (จอย/ปุ่มบนจอ) หรือ F6 (คีย์บอร์ด) = พันแผล  (เลือดค่อยๆ เพิ่ม)
// RB (จอย/ปุ่มบนจอ) หรือ F7 (คีย์บอร์ด) = ใส่เกราะ (เกราะ 100 + ตัวถึกขึ้น)
// วิ่งอยู่แล้วกดปุ่มใส่เกราะ = สไลด์พุ่งไปข้างหน้า
public class SurvivalMoves : Script
{
    // ================= ตั้งค่า =================
    // ปุ่มคีย์บอร์ด (ใส่เป็นรหัสปุ่ม Windows)
    // ตัวอย่างรหัส: F5=0x74  F6=0x75  F7=0x76  F8=0x77  F9=0x78
    //               H=0x48  G=0x47  X=0x58  Z=0x5A  R=0x52  B=0x42
    const int BANDAGE_VK = 0x75;  // F6 = พันแผล
    const int ARMOR_VK   = 0x76;  // F7 = ใส่เกราะ / วิ่งอยู่แล้วกด = สไลด์

    // ปุ่มจอย / ปุ่มบนหน้าจอ (true = เปิดใช้, false = ปิด)
    // เปิดไว้จะทำให้ปุ่มย่อง (L3) กับปุ่มเข้าที่กำบัง (RB) ใช้งานปกติไม่ได้
    const bool    USE_GAMEPAD = true;
    const Control BANDAGE_BTN = Control.Duck;    // L3 (ปุ่ม Stealth)
    const Control ARMOR_BTN   = Control.Cover;   // RB (ปุ่ม Cover)

    // สไลด์
    const bool  SLIDE_DOUBLE_TAP = false; // false = วิ่งแล้วกดครั้งเดียวสไลด์เลย / true = ต้องกดรัว 2 ครั้ง
    const int   DOUBLE_TAP_MS    = 900;   // (ใช้เมื่อเปิดกดรัว) กดรัวภายในกี่ ms
    const int   SLIDE_STYLE      = 4;     // 0 = สุ่ม, 1 = ย่อตัวไถล, 2 = ล้มไถลไปกับพื้น, 3 = พุ่งตัว, 4 = คุกเข่าไถล
    const int   SLIDE_MS         = 900;   // ระยะเวลาสไลด์ (แบบ 1 และ 3)
    const int   RAGDOLL_MS       = 800;   // ระยะเวลาสไลด์แบบล้มไถล (แบบ 2)
    const float SLIDE_SPEED      = 8f;    // ความเร็วเริ่มต้น (เพิ่ม = พุ่งไกลขึ้น)
    const int   SLIDE_COOLDOWN   = 400;   // เว้นช่วงก่อนสไลด์ได้อีก (ms)

    // พันแผล
    const int   BANDAGE_TOTAL   = 60;    // เลือดที่ได้รวม
    const int   BANDAGE_STEP    = 10;    // เพิ่มทีละเท่าไหร่
    const int   BANDAGE_TICK_MS = 500;   // เพิ่มทุกกี่ ms

    // ใส่เกราะ
    const int   ARMOR_ANIM_MS   = 1800;  // เวลาท่าใส่เกราะ

    // ท่าทาง (ถ้าไม่ถูกใจ เปลี่ยนชื่อได้)
    const string BANDAGE_DICT  = "amb@medic@standing@kneel@idle_a";
    const string BANDAGE_ANIM  = "idle_a";
    const int    BANDAGE_FLAG  = 1;     // 1 = เล่นทั้งตัว วนซ้ำ
    const string BANDAGE_DICT2 = "amb@medic@standing@tendtodead@base";   // ท่าสำรอง
    const string BANDAGE_ANIM2 = "base";
    const string ARMOR_DICT    = "clothingtie";
    const string ARMOR_ANIM    = "try_tie_positive_a";
    // ท่าสไลด์: ท่าฉลองคุกเข่าไถลของ Arena (เลือกสุ่ม a / b / c)
    const string SLIDE_DICT    = "anim@arena@celeb@flat@solo@no_props@";
    static readonly string[] SLIDE_ANIMS = { "slide_a_player_a", "slide_b_player_a", "slide_c_player_a" };
    // ท่าสำรองถ้าเกมไม่มีท่าด้านบน: คุกเข่าแบบหมอ
    const string KNEEL_DICT    = "amb@medic@standing@kneel@idle_a";
    const string KNEEL_ANIM    = "idle_a";
    const string DIVE_DICT     = "move_jump";
    const string DIVE_ANIM     = "dive_start_run";
    // ===========================================

    static readonly Random rng = new Random();

    bool kbBandage, kbArmor, prevF6, prevF7;

    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vKey);

    bool bandaging; int healLeft; int nextHeal; int bandageEnd;
    bool armoring; int armorEnd;
    bool pendingArmor; int lastTap;
    string curDict = ""; string curAnim = "";
    bool sliding; int slideStart; int slideMs; int slideStyle; int slideReadyAt; Vector3 slideDir;

    public SurvivalMoves()
    {
        Tick += OnTick;
    }

    // ---------- ตัวช่วย ----------
    bool Pressed(Control c)
    {
        return Function.Call<bool>(Hash.IS_DISABLED_CONTROL_JUST_PRESSED, 0, (int)c);
    }

    bool LoadDict(string dict)
    {
        Function.Call(Hash.REQUEST_ANIM_DICT, dict);
        int tries = 0;
        while (!Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, dict) && tries++ < 50)
            Script.Yield();
        return Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, dict);
    }

    void PlayAnim(Ped p, string dict, string anim, int ms, int flag)
    {
        if (!LoadDict(dict)) return;
        Function.Call(Hash.TASK_PLAY_ANIM, p, dict, anim, 8f, -8f, ms, flag, 0f, false, false, false);
    }

    void PlayAnimFb(Ped p, string d1, string a1, int f1, string d2, string a2, int f2, int ms)
    {
        if (LoadDict(d1))
            Function.Call(Hash.TASK_PLAY_ANIM, p, d1, a1, 8f, -8f, ms, f1, 0f, false, false, false);
        else if (LoadDict(d2))
            Function.Call(Hash.TASK_PLAY_ANIM, p, d2, a2, 8f, -8f, ms, f2, 0f, false, false, false);
    }

    void StopAnim(Ped p, string dict, string anim)
    {
        Function.Call(Hash.STOP_ANIM_TASK, p, dict, anim, 1f);
    }

    void SetDuck(Ped p, bool on)
    {
        Function.Call((Hash)0x030983CA, p, on);   // SET_PED_DUCKING
    }

    // ใช้คำสั่งพื้นฐานของเกม (native) แทนฟังก์ชันของ ScriptHookVDotNet
    // เพื่อให้ใช้ได้กับ ScriptHookVDotNet v3 ทุกเวอร์ชัน
    int NowMs()                    { return Function.Call<int>(Hash.GET_GAME_TIMER); }
    int GetHealth(Ped p)           { return Function.Call<int>(Hash.GET_ENTITY_HEALTH, p); }
    int GetMaxHealth(Ped p)        { return Function.Call<int>(Hash.GET_ENTITY_MAX_HEALTH, p); }
    void SetHealth(Ped p, int v)   { Function.Call(Hash.SET_ENTITY_HEALTH, p, v); }
    int GetArmor(Ped p)            { return Function.Call<int>(Hash.GET_PED_ARMOUR, p); }
    void SetArmor(Ped p, int v)    { Function.Call(Hash.SET_PED_ARMOUR, p, v); }
    bool IsSprinting(Ped p)        { return Function.Call<bool>(Hash.IS_PED_SPRINTING, p); }
    bool InVehicle(Ped p)          { return Function.Call<bool>(Hash.IS_PED_IN_ANY_VEHICLE, p, false); }
    Vector3 GetForward(Ped p)      { return Function.Call<Vector3>(Hash.GET_ENTITY_FORWARD_VECTOR, p); }
    float GetVelZ(Ped p)           { return Function.Call<Vector3>(Hash.GET_ENTITY_VELOCITY, p).Z; }
    void SetVelocity(Ped p, float x, float y, float z)
    {
        Function.Call(Hash.SET_ENTITY_VELOCITY, p, x, y, z);
    }

    void Subtitle(string text)
    {
        Function.Call(Hash.BEGIN_TEXT_COMMAND_PRINT, "STRING");
        Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, text);
        Function.Call(Hash.END_TEXT_COMMAND_PRINT, 2000, true);
    }

    // ---------- ลูปหลัก ----------
    void OnTick(object sender, EventArgs e)
    {
        try
        {
            Tick2();
        }
        catch (Exception)
        {
            // กันสคริปต์ล้มทั้งตัวถ้าเกิดข้อผิดพลาดชั่วคราว
        }
    }

    void Tick2()
    {
        Ped p = Game.Player.Character;
        if (p == null || !p.Exists() || p.IsDead || InVehicle(p)) { sliding = false; return; }

        // กันปุ่มจอยทำงานอย่างอื่น (เช่น ย่อง / เข้าที่กำบัง) เฉพาะตอนเปิดใช้ปุ่มจอย
        if (USE_GAMEPAD)
        {
            Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, (int)BANDAGE_BTN, true);
            Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, (int)ARMOR_BTN, true);
        }

        bool f6 = (GetAsyncKeyState(BANDAGE_VK) & 0x8000) != 0;
        bool f7 = (GetAsyncKeyState(ARMOR_VK) & 0x8000) != 0;
        kbBandage = f6 && !prevF6; kbArmor = f7 && !prevF7;
        prevF6 = f6; prevF7 = f7;

        bool bandagePressed = (USE_GAMEPAD && Pressed(BANDAGE_BTN)) || kbBandage;
        bool armorPressed = (USE_GAMEPAD && Pressed(ARMOR_BTN)) || kbArmor;
        kbBandage = false; kbArmor = false;
        int now = NowMs();

        // ---------- พันแผล ----------
        if (bandagePressed && !bandaging && !sliding)
        {
            if (GetHealth(p) >= GetMaxHealth(p))
                Subtitle("เลือดเต็มอยู่แล้ว");
            else
            {
                bandaging = true;
                healLeft = BANDAGE_TOTAL;
                nextHeal = now + BANDAGE_TICK_MS;
                bandageEnd = now + (BANDAGE_TOTAL / BANDAGE_STEP) * BANDAGE_TICK_MS;
                PlayAnimFb(p, BANDAGE_DICT, BANDAGE_ANIM, BANDAGE_FLAG, BANDAGE_DICT2, BANDAGE_ANIM2, 49, bandageEnd - now);
            }
        }
        if (bandaging)
        {
            if (now >= nextHeal && healLeft > 0)
            {
                SetHealth(p, Math.Min(GetMaxHealth(p), GetHealth(p) + BANDAGE_STEP));
                healLeft -= BANDAGE_STEP;
                nextHeal = now + BANDAGE_TICK_MS;
            }
            if (healLeft <= 0 || now >= bandageEnd || GetHealth(p) >= GetMaxHealth(p))
            {
                bandaging = false;
                StopAnim(p, BANDAGE_DICT, BANDAGE_ANIM);
                StopAnim(p, BANDAGE_DICT2, BANDAGE_ANIM2);
            }
        }

        // ---------- ใส่เกราะ / สไลด์ ----------
        if (armorPressed)
        {
            if (IsSprinting(p))
            {
                if (!SLIDE_DOUBLE_TAP)
                {
                    TryStartSlide(p, now);
                }
                else if (lastTap > 0 && now - lastTap < DOUBLE_TAP_MS)
                {
                    pendingArmor = false;
                    lastTap = 0;
                    TryStartSlide(p, now);
                }
                else
                {
                    lastTap = now;
                    pendingArmor = true;   // รอดูว่าจะกดซ้ำเป็นสไลด์ไหม
                }
            }
            else StartArmor(p, now);
        }
        if (pendingArmor && now - lastTap > DOUBLE_TAP_MS)
        {
            pendingArmor = false;
            lastTap = 0;
            if (!sliding) StartArmor(p, now);
        }
        if (armoring && now >= armorEnd)
        {
            armoring = false;
            SetArmor(p, 100);
            StopAnim(p, ARMOR_DICT, ARMOR_ANIM);
            Subtitle("ใส่เกราะแล้ว");
        }

        // ตัวถึกขึ้นเมื่อมีเกราะ: ไม่โดนฮิตคริติคอล (เฮดช็อตตายทันที) และล้มยากขึ้น
        bool armored = GetArmor(p) > 0;
        Function.Call(Hash.SET_PED_SUFFERS_CRITICAL_HITS, p, !armored);
        Function.Call(Hash.SET_PED_CAN_RAGDOLL_FROM_PLAYER_IMPACT, p, !armored);

        // ---------- ระหว่างสไลด์ ----------
        if (sliding)
        {
            int t = now - slideStart;
            if (t >= slideMs)
            {
                EndSlide(p, now);
            }
            else
            {
                if (slideStyle == 1) SetDuck(p, true);   // ย่อตัวต่ำตลอดช่วงสไลด์

                float speed = SLIDE_SPEED * (1f - (float)t / slideMs) + 1.5f;
                if (slideStyle == 2) speed *= 1.2f;      // ล้มไถลต้องแรงกว่านิดหน่อยเพราะมีแรงเสียดทาน
                SetVelocity(p, slideDir.X * speed, slideDir.Y * speed, GetVelZ(p));
            }
        }
    }

    // ---------- ใส่เกราะ ----------
    void StartArmor(Ped p, int now)
    {
        if (armoring || sliding) return;
        if (GetArmor(p) >= 100) { Subtitle("เกราะเต็มอยู่แล้ว"); return; }
        armoring = true;
        armorEnd = now + ARMOR_ANIM_MS;
        PlayAnim(p, ARMOR_DICT, ARMOR_ANIM, ARMOR_ANIM_MS, 49);
    }

    // ---------- สไลด์ ----------
    void TryStartSlide(Ped p, int now)
    {
        if (sliding || now < slideReadyAt) return;
        StartSlide(p, now);
    }

    void StartSlide(Ped p, int now)
    {
        // ยกเลิกท่าอื่นที่ค้างอยู่
        if (armoring) { armoring = false; StopAnim(p, ARMOR_DICT, ARMOR_ANIM); }
        if (bandaging)
        {
            bandaging = false;
            StopAnim(p, BANDAGE_DICT, BANDAGE_ANIM);
            StopAnim(p, BANDAGE_DICT2, BANDAGE_ANIM2);
        }

        slideStyle = (SLIDE_STYLE == 0) ? rng.Next(1, 5) : SLIDE_STYLE;
        slideMs = (slideStyle == 2) ? RAGDOLL_MS : SLIDE_MS;
        sliding = true;
        slideStart = now;
        slideDir = GetForward(p);

        if (slideStyle == 1)
        {
            SetDuck(p, true);   // ย่อตัวต่ำแล้วไถล
        }
        else if (slideStyle == 4)
        {
            // คุกเข่าไถลไปข้างหน้า: ลองท่า Arena ก่อน ถ้าไม่มีใช้ท่าคุกเข่าแบบหมอ ถ้าไม่มีอีกใช้ท่าย่อตัว
            if (LoadDict(SLIDE_DICT))
            {
                curDict = SLIDE_DICT;
                curAnim = SLIDE_ANIMS[rng.Next(SLIDE_ANIMS.Length)];
                Function.Call(Hash.TASK_PLAY_ANIM, p, curDict, curAnim, 8f, -8f, SLIDE_MS, 0, 0f, false, false, false);
            }
            else if (LoadDict(KNEEL_DICT))
            {
                curDict = KNEEL_DICT;
                curAnim = KNEEL_ANIM;
                Function.Call(Hash.TASK_PLAY_ANIM, p, curDict, curAnim, 8f, -8f, SLIDE_MS, 1, 0f, false, false, false);
            }
            else
            {
                slideStyle = 1;
                SetDuck(p, true);
            }
        }
        else if (slideStyle == 2)
        {
            // ปล่อยตัวล้มลงพื้นแล้วไถลไปตามแรงเฉื่อย
            Function.Call(Hash.SET_PED_TO_RAGDOLL, p, RAGDOLL_MS, RAGDOLL_MS, 0, false, false, false);
        }
        else
        {
            // พุ่งตัวไปข้างหน้า
            PlayAnim(p, DIVE_DICT, DIVE_ANIM, SLIDE_MS, 0);
        }
    }

    void EndSlide(Ped p, int now)
    {
        sliding = false;
        slideReadyAt = now + SLIDE_COOLDOWN;
        if (slideStyle == 1) SetDuck(p, false);
        if (slideStyle == 3) StopAnim(p, DIVE_DICT, DIVE_ANIM);
        if (slideStyle == 4) StopAnim(p, curDict, curAnim);
    }
}
