using System.Globalization;
using Xperience.Core.Actions;
using Xperience.Core.Input;

namespace Xperience.Tests;

/// <summary>
/// The numbers this program uses to talk to the kernel's input layer, against the kernel's own headers. The
/// test run compiles a small C program that prints them; its output is read here.
/// </summary>
public class KernelFactsTests
{
    static Dictionary<string, long> Facts()
    {
        string? file = Environment.GetEnvironmentVariable("XPERIENCE_KERNEL_FACTS");
        Assert.True(file != null && File.Exists(file), "The kernel's numbers were not provided: run the tests through tools/test.sh.");
        return File.ReadAllLines(file!).Select(l => l.Split('=')).Where(p => p.Length == 2)
            .ToDictionary(p => p[0], p => long.Parse(p[1], CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Requests_sizes_and_event_types_are_the_kernels()
    {
        var facts = Facts();
        (string Name, long Ours)[] pairs =
        [
            ("UI_SET_EVBIT", KernelInput.UiSetEvBit), ("UI_SET_KEYBIT", KernelInput.UiSetKeyBit), ("UI_SET_RELBIT", KernelInput.UiSetRelBit),
            ("UI_DEV_SETUP", KernelInput.UiDevSetup), ("UI_DEV_CREATE", KernelInput.UiDevCreate), ("UI_DEV_DESTROY", KernelInput.UiDevDestroy),
            ("EVIOCGABS_X", KernelInput.EviocgAbsX), ("EVIOCGABS_Y", KernelInput.EviocgAbsY),
            ("sizeof_input_event", KernelInput.EventSize), ("sizeof_uinput_setup", KernelInput.SetupSize), ("sizeof_input_absinfo", KernelInput.AbsInfoSize),
            ("UINPUT_MAX_NAME_SIZE", KernelInput.NameLength), ("BUS_VIRTUAL", KernelInput.BusVirtual),
            ("USBDEVFS_CONTROL", KernelInput.UsbdevfsControl), ("sizeof_usb_control", KernelInput.UsbControlSize),
            ("sizeof_usb_control", System.Runtime.CompilerServices.Unsafe.SizeOf<Xperience.Core.Tablets.UsbControl>()),
            ("offsetof_usb_control_timeout", (long)System.Runtime.InteropServices.Marshal.OffsetOf<Xperience.Core.Tablets.UsbControl>(nameof(Xperience.Core.Tablets.UsbControl.TimeoutMs))),
            ("offsetof_usb_control_data", (long)System.Runtime.InteropServices.Marshal.OffsetOf<Xperience.Core.Tablets.UsbControl>(nameof(Xperience.Core.Tablets.UsbControl.Data))),
            ("offsetof_event_type", 16), ("offsetof_event_code", 18), ("offsetof_event_value", 20), ("offsetof_setup_name", 8), ("offsetof_absinfo_maximum", 8),
            ("EV_SYN", KernelInput.EvSyn), ("EV_KEY", KernelInput.EvKey), ("EV_REL", KernelInput.EvRel),
            ("REL_X", KernelInput.RelX), ("REL_Y", KernelInput.RelY), ("REL_WHEEL", KernelInput.RelWheel), ("REL_HWHEEL", KernelInput.RelHWheel),
            ("BTN_MISC", KernelInput.BtnMisc), ("BTN_LEFT", KernelInput.BtnLeft), ("BTN_RIGHT", KernelInput.BtnRight), ("BTN_MIDDLE", KernelInput.BtnMiddle),
            ("BTN_STYLUS", Xperience.Core.Desktop.Assignments.PenLowerButton), ("BTN_STYLUS2", Xperience.Core.Desktop.Assignments.PenUpperButton),
            ("KEY_LEFTCTRL", KeyTable.LeftCtrl), ("KEY_LEFTSHIFT", KeyTable.LeftShift), ("KEY_LEFTALT", KeyTable.LeftAlt),
            ("KEY_RIGHTALT", KeyTable.RightAlt), ("KEY_LEFTMETA", KeyTable.LeftSuper),
        ];
        Assert.All(pairs, pair => Assert.True(facts[pair.Name] == pair.Ours, $"{pair.Name}: the kernel has {facts[pair.Name]}, this program has {pair.Ours}"));
    }

    [Fact]
    public void Every_key_a_shortcut_can_name_has_the_kernels_code()
    {
        var facts = Facts();
        var kernelNames = new Dictionary<string, string>
        {
            ["Enter"] = "ENTER", ["Esc"] = "ESC", ["Backspace"] = "BACKSPACE", ["Delete"] = "DELETE", ["Insert"] = "INSERT", ["Home"] = "HOME",
            ["End"] = "END", ["PageUp"] = "PAGEUP", ["PageDown"] = "PAGEDOWN", ["Up"] = "UP", ["Down"] = "DOWN", ["Left"] = "LEFT", ["Right"] = "RIGHT",
            ["Space"] = "SPACE", ["Tab"] = "TAB", ["-"] = "MINUS", ["="] = "EQUAL", ["["] = "LEFTBRACE", ["]"] = "RIGHTBRACE", [";"] = "SEMICOLON",
            ["'"] = "APOSTROPHE", ["`"] = "GRAVE", ["\\"] = "BACKSLASH", [","] = "COMMA", ["."] = "DOT", ["/"] = "SLASH",
        };
        Assert.All(KeyTable.Keys, key =>
        {
            string name = "KEY_" + kernelNames.GetValueOrDefault(key.Name, key.Name.ToUpperInvariant());
            Assert.True(facts.ContainsKey(name), $"no kernel name for {key.Name}");
            Assert.True(facts[name] == key.Code, $"{key.Name}: the kernel has {facts[name]} for {name}, this program has {key.Code}");
        });
    }
}
