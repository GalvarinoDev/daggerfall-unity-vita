// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// The Vita system keyboard (IME dialog) for DFU text boxes: character names, save names, searches. Unity's TouchScreenKeyboard has
// no Vita backend in the release player, and Unity links no IME library, so the dialog calls live in a tiny native plugin
// (vita/plugins/vitaime → Assets/Plugins/PSVita/VitaIme.suprx + VitaIme_stub.a). Unity links a plugin stub only for DllImports that name
// the plugin, not "__Internal". The player already pumps sceCommonDialogUpdate.
// The keyboard opens when a text box becomes active; Triangle opens it again (after a cancel).

#if UNITY_PSP2 && !UNITY_EDITOR
using System;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using DaggerfallWorkshop.Game.UserInterface;

public static class VitaKeyboard
{
    // UTF-16 buffers by pointer: IL2CPP marshals char[]/string as 1-byte chars despite CharSet.Unicode (typed "Rai", got "R")
    [DllImport("VitaIme")] static extern int VitaIme_Open(IntPtr title, IntPtr text, int maxLength, int numeric);
    [DllImport("VitaIme")] static extern int VitaIme_Status();
    [DllImport("VitaIme")] static extern int VitaIme_Finish(IntPtr text, int capacity);

    const int statusFinished = 2, buttonEnter = 2, capacity = 128;   // the plugin's MAX_TEXT

    static TextBox lastBox, owner;
    static int lastFrame = -10, submitFrame = -10;

    /// <summary>True in the frame the keyboard closed with its Enter button: windows that wait for Return take it as Return
    ///.</summary>
    public static bool Submitted
    {
        get { return Time.frameCount == submitFrame; }
    }

    /// <summary>Called from TextBox.Update while the box takes input. True = the keyboard has the input this frame.</summary>
    public static bool Update(TextBox box)
    {
        bool newlyActive = box != lastBox || Time.frameCount - lastFrame > 1;
        lastBox = box;
        lastFrame = Time.frameCount;

        if (owner != null)
        {
            if (VitaIme_Status() == statusFinished)
                Finish();
            return true;
        }
        if (newlyActive || Input.GetKeyDown(KeyCode.JoystickButton3))   // Triangle
            Open(box);
        return owner != null;
    }

    static void Open(TextBox box)
    {
        IntPtr title = Marshal.StringToHGlobalUni("Daggerfall Unity");
        IntPtr text = Marshal.StringToHGlobalUni(box.Text ?? string.Empty);
        int error = VitaIme_Open(title, text, Mathf.Clamp(box.MaxCharacters, 1, capacity), box.Numeric ? 1 : 0);   // the plugin copies both
        Marshal.FreeHGlobal(title);
        Marshal.FreeHGlobal(text);
        if (error < 0)
        {
            Debug.LogWarningFormat("VitaKeyboard: sceImeDialogInit failed 0x{0:x8}", error);
            return;
        }
        owner = box;
    }

    static void Finish()
    {
        IntPtr result = Marshal.AllocHGlobal((capacity + 1) * 2);
        if (VitaIme_Finish(result, capacity) == buttonEnter)
        {
            owner.Text = Filter(owner, Marshal.PtrToStringUni(result) ?? string.Empty);
            submitFrame = Time.frameCount;
        }
        Marshal.FreeHGlobal(result);
        owner = null;
    }

    // Same limits as typed input: length, upper case, digits for numeric boxes
    static string Filter(TextBox box, string text)
    {
        if (box.UpperOnly)
            text = text.ToUpperInvariant();
        if (box.Numeric)
        {
            var digits = new StringBuilder();
            foreach (char c in text)
                if (char.IsDigit(c) || (box.NumericMode != NumericMode.Natural && (c == '-' || c == '.')))
                    digits.Append(c);
            text = digits.ToString();
        }
        return text.Length > box.MaxCharacters ? text.Substring(0, box.MaxCharacters) : text;
    }
}
#endif
