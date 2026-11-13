using System;
using System.Runtime.InteropServices;
using GTA;
using GTA.Math;
using GTA.Native;

// SurvivalMoves - ScriptHookVDotNet v3
// L3 (จอย) / F6 (คีย์บอร์ด) = พันแผล  (เลือดค่อยๆ เพิ่ม)
// RB (จอย) / F7 (คีย์บอร์ด) = ใส่เกราะ (เกราะ 100 + ตัวถึกขึ้น)
// วิ่งอยู่แล้วกดปุ่มใส่เกราะ (RB / F7) = สไลด์พุ่งไปข้างหน้า (สุ่มท่าได้หลายแบบ)
public class SurvivalMoves : Script
{
    // ================= ตั้งค่า =================
    // ปุ่มจอย
    const Control BANDAGE_BTN = Control.Duck;    // L3 (ปุ่ม Stealth)
    const Control ARMOR_BTN   = Control.Cover;   // RB (ปุ่ม Cover)
    // ปุ่มคีย์บอร์ด (รหัสปุ่ม Windows)
    const int BANDAGE_VK = 0x75;  // F6
    const int ARMOR_VK   = 0x76;  // F7

    // สไลด์
    const bool  SLIDE_DOUBLE_TAP = false; // false = วิ่งแล้วกดครั้งเดียวสไลด์เลย / true = ต้องกดรัว 2 ครั้ง
    const int   DOUBLE_TAP_MS    = 900;   // (ใช้เมื่อเปิดกดรัว) กดรัวภายในกี่ ms
    const int   SLIDE_STYLE      = 1;     // 0 = สุ่ม, 1 = ย่อตัวไถล, 2 = ล้มไถลไปกับพื้น, 3 = พุ่งตัว, 4 = คุกเข่าไถล
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
    const string KNEEL_DICT    = "amb@medic@standing@kneel@idle_a";   // ท่าคุกเข่าตอนไถล
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

    // ---------- ลูปหลัก ----------
    void OnTick(object sender, EventArgs e)
    {
        Ped p = Game.Player.Character;
        if (p == null || !p.Exists() || p.IsDead || p.IsInVehicle()) { sliding = false; return; }

        // กันปุ่มทำงานอย่างอื่น (เช่น ย่อง / เข้าที่กำบัง)
        Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, (int)BANDAGE_BTN, true);
        Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, (int)ARMOR_BTN, true);

        bool f6 = (GetAsyncKeyState(BANDAGE_VK) & 0x8000) != 0;
        bool f7 = (GetAsyncKeyState(ARMOR_VK) & 0x8000) != 0;
        kbBandage = f6 && !prevF6; kbArmor = f7 && !prevF7;
        prevF6 = f6; prevF7 = f7;

        bool bandagePressed = Pressed(BANDAGE_BTN) || kbBandage;
        bool armorPressed = Pressed(ARMOR_BTN) || kbArmor;
        kbBandage = false; kbArmor = false;
        int now = Game.GameTime;

        // ---------- พันแผล ----------
        if (bandagePressed && !bandaging && !sliding)
        {
            if (p.Health >= p.MaxHealth)
                GTA.UI.Screen.ShowSubtitle("เลือดเต็มอยู่แล้ว");
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
                p.Health = Math.Min(p.MaxHealth, p.Health + BANDAGE_STEP);
                healLeft -= BANDAGE_STEP;
                nextHeal = now + BANDAGE_TICK_MS;
            }
            if (healLeft <= 0 || now >= bandageEnd || p.Health >= p.MaxHealth)
            {
                bandaging = false;
                StopAnim(p, BANDAGE_DICT, BANDAGE_ANIM);
                StopAnim(p, BANDAGE_DICT2, BANDAGE_ANIM2);
            }
        }

        // ---------- ใส่เกราะ / สไลด์ ----------
        if (armorPressed)
        {
            if (p.IsSprinting)
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
            p.Armor = 100;
            StopAnim(p, ARMOR_DICT, ARMOR_ANIM);
            GTA.UI.Screen.ShowSubtitle("ใส่เกราะแล้ว");
        }

        // ตัวถึกขึ้นเมื่อมีเกราะ: ไม่โดนฮิตคริติคอล (เฮดช็อตตายทันที) และล้มยากขึ้น
        bool armored = p.Armor > 0;
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
                Vector3 v = slideDir * speed;
                v.Z = p.Velocity.Z;
                p.Velocity = v;
            }
        }
    }

    // ---------- ใส่เกราะ ----------
    void StartArmor(Ped p, int now)
    {
        if (armoring || sliding) return;
        if (p.Armor >= 100) { GTA.UI.Screen.ShowSubtitle("เกราะเต็มอยู่แล้ว"); return; }
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
        slideDir = p.ForwardVector;

        if (slideStyle == 1)
        {
            SetDuck(p, true);   // ย่อตัวต่ำแล้วไถล
        }
        else if (slideStyle == 4)
        {
            // คุกเข่าแล้วไถลไปข้างหน้า (ถ้าเกมไม่มีท่านี้ ถอยไปใช้ท่าย่อตัวแทน)
            if (LoadDict(KNEEL_DICT))
                Function.Call(Hash.TASK_PLAY_ANIM, p, KNEEL_DICT, KNEEL_ANIM, 8f, -8f, SLIDE_MS, 1, 0f, false, false, false);
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
        if (slideStyle == 4) StopAnim(p, KNEEL_DICT, KNEEL_ANIM);
    }
}
