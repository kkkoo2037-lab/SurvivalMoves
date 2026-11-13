using System;
using System.Runtime.InteropServices;
using GTA;
using GTA.Math;
using GTA.Native;

// SurvivalMoves - ScriptHookVDotNet v3
// L3 (จอย) / F6 (คีย์บอร์ด) = พันแผล  (เลือดค่อยๆ เพิ่ม)
// RB (จอย) / F7 (คีย์บอร์ด) = ใส่เกราะ (เกราะ 100 + ตัวถึกขึ้น)
// วิ่งอยู่แล้วกดปุ่มใส่เกราะรัว 2 ครั้ง = สไลด์พุ่งไปข้างหน้า
public class SurvivalMoves : Script
{
    // ---------- ตั้งค่าปุ่ม / ค่าต่างๆ ----------
    const Control BANDAGE_BTN = Control.Duck;    // L3 (ปุ่ม Stealth)
    const Control ARMOR_BTN   = Control.Cover;   // RB (ปุ่ม Cover)
    const int BANDAGE_VK = 0x75;  // F6
    const int ARMOR_VK   = 0x76;  // F7

    const int   DOUBLE_TAP_MS   = 600;   // กดรัวภายในกี่ ms ถึงนับเป็นสไลด์
    const int   BANDAGE_TOTAL   = 60;    // เลือดที่ได้รวม
    const int   BANDAGE_STEP    = 10;    // เพิ่มทีละเท่าไหร่
    const int   BANDAGE_TICK_MS = 500;   // เพิ่มทุกกี่ ms
    const int   ARMOR_ANIM_MS   = 1800;  // เวลาท่าใส่เกราะ
    const int   SLIDE_MS        = 900;   // ระยะเวลาสไลด์
    const float SLIDE_SPEED     = 8f;    // ความเร็วเริ่มต้นตอนสไลด์

    // ท่าทาง (ถ้าไม่ถูกใจ เปลี่ยนชื่อได้)
    // พันแผล: ท่าคุกเข่าทำแผล (ถ้าเกมไม่มี จะถอยไปใช้ท่าสำรอง)
    const string BANDAGE_DICT  = "amb@medic@standing@kneel@idle_a";
    const string BANDAGE_ANIM  = "idle_a";
    const int    BANDAGE_FLAG  = 1;     // 1 = เล่นทั้งตัว วนซ้ำ
    const string BANDAGE_DICT2 = "amb@medic@standing@tendtodead@base";
    const string BANDAGE_ANIM2 = "base";
    const string ARMOR_DICT   = "clothingtie";
    const string ARMOR_ANIM   = "try_tie_positive_a";
    // สไลด์: ท่าไถลต่ำ (ถ้าเกมไม่มีแพ็กนี้ จะถอยไปใช้ท่าสำรอง)
    const string SLIDE_DICT   = "parkour@anims";
    const string SLIDE_ANIM   = "slide";
    const string SLIDE_DICT2  = "move_jump";
    const string SLIDE_ANIM2  = "dive_start_run";
    // -------------------------------------------

    bool kbBandage, kbArmor, prevF6, prevF7;

    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vKey);

    bool bandaging; int healLeft; int nextHeal; int bandageEnd;
    bool armoring; int armorEnd;
    bool pendingArmor; int lastTap;
    bool sliding; int slideStart; Vector3 slideDir;

    public SurvivalMoves()
    {
        Tick += OnTick;
    }

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
                StopAnim(p, BANDAGE_DICT, BANDAGE_ANIM); StopAnim(p, BANDAGE_DICT2, BANDAGE_ANIM2);
            }
        }

        // ---------- ใส่เกราะ / สไลด์ ----------
        if (armorPressed)
        {
            if (p.IsSprinting)
            {
                if (lastTap > 0 && now - lastTap < DOUBLE_TAP_MS)
                {
                    pendingArmor = false;
                    lastTap = 0;
                    StartSlide(p, now);
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

        // ---------- สไลด์ ----------
        if (sliding)
        {
            int t = now - slideStart;
            if (t >= SLIDE_MS)
            {
                sliding = false;
                StopAnim(p, SLIDE_DICT, SLIDE_ANIM); StopAnim(p, SLIDE_DICT2, SLIDE_ANIM2);
            }
            else
            {
                float speed = SLIDE_SPEED * (1f - (float)t / SLIDE_MS) + 1.5f;
                Vector3 v = slideDir * speed;
                v.Z = p.Velocity.Z;
                p.Velocity = v;
            }
        }
    }

    void StartArmor(Ped p, int now)
    {
        if (armoring || sliding) return;
        if (p.Armor >= 100) { GTA.UI.Screen.ShowSubtitle("เกราะเต็มอยู่แล้ว"); return; }
        armoring = true;
        armorEnd = now + ARMOR_ANIM_MS;
        PlayAnim(p, ARMOR_DICT, ARMOR_ANIM, ARMOR_ANIM_MS, 49);
    }

    void StartSlide(Ped p, int now)
    {
        if (sliding) return;
        // ยกเลิกท่าอื่นที่ค้างอยู่
        if (armoring) { armoring = false; StopAnim(p, ARMOR_DICT, ARMOR_ANIM); }
        if (bandaging) { bandaging = false; StopAnim(p, BANDAGE_DICT, BANDAGE_ANIM); StopAnim(p, BANDAGE_DICT2, BANDAGE_ANIM2); }
        sliding = true;
        slideStart = now;
        slideDir = p.ForwardVector;
        PlayAnimFb(p, SLIDE_DICT, SLIDE_ANIM, 0, SLIDE_DICT2, SLIDE_ANIM2, 0, SLIDE_MS);
    }
}
