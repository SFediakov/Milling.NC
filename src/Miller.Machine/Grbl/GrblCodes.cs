namespace Miller.Machine.Grbl;

// Grbl v1.1 error and alarm codes with the descriptions of the Grbl documentation. Controllers
// derived from Grbl (grblHAL, FluidNC) add codes above these; they are shown by number.
public static class GrblCodes
{
    private static readonly Dictionary<int, string> Errors = new()
    {
        [1] = "G-code words consist of a letter and a value. Letter was not found.",
        [2] = "Missing the expected G-code word value or numeric value format is not valid.",
        [3] = "Grbl '$' system command was not recognized or supported.",
        [4] = "Negative value received for an expected positive value.",
        [5] = "Homing cycle failure. Homing is not enabled via settings.",
        [6] = "Minimum step pulse time must be greater than 3usec.",
        [7] = "An EEPROM read failed. Auto-restoring affected EEPROM to default values.",
        [8] = "Grbl '$' command cannot be used unless Grbl is IDLE.",
        [9] = "G-code commands are locked out during alarm or jog state.",
        [10] = "Soft limits cannot be enabled without homing also enabled.",
        [11] = "Max characters per line exceeded. Received command line was not executed.",
        [12] = "Grbl '$' setting value cause the step rate to exceed the maximum supported.",
        [13] = "Safety door detected as opened and door state initiated.",
        [14] = "Build info or startup line exceeded EEPROM line length limit.",
        [15] = "Jog target exceeds machine travel. Jog command has been ignored.",
        [16] = "Jog command has no '=' or contains prohibited g-code.",
        [17] = "Laser mode requires PWM output.",
        [20] = "Unsupported or invalid g-code command found in block.",
        [21] = "More than one g-code command from same modal group found in block.",
        [22] = "Feed rate has not yet been set or is undefined.",
        [23] = "G-code command in block requires an integer value.",
        [24] = "More than one g-code command that requires axis words found in block.",
        [25] = "Repeated g-code word found in block.",
        [26] = "No axis words found in block for g-code command or current modal state.",
        [27] = "Line number value is invalid.",
        [28] = "G-code command is missing a required value word.",
        [29] = "G59.x work coordinate systems are not supported.",
        [30] = "G53 only allowed with G0 and G1 motion modes.",
        [31] = "Axis words found in block when no command or current modal state uses them.",
        [32] = "G2 and G3 arcs require at least one in-plane axis word.",
        [33] = "Motion command target is invalid.",
        [34] = "Arc radius value is invalid.",
        [35] = "G2 and G3 arcs require at least one in-plane offset word.",
        [36] = "Unused value words found in block.",
        [37] = "G43.1 dynamic tool length offset is not assigned to configured tool length axis.",
        [38] = "Tool number greater than max supported value.",
    };

    private static readonly Dictionary<int, string> Alarms = new()
    {
        [1] = "Hard limit has been triggered. Machine position is likely lost due to sudden halt. Re-homing is highly recommended.",
        [2] = "Soft limit alarm. G-code motion target exceeds machine travel. Machine position retained. Alarm may be safely unlocked.",
        [3] = "Reset while in motion. Machine position is likely lost due to sudden halt. Re-homing is highly recommended.",
        [4] = "Probe fail. Probe is not in the expected initial state before starting probe cycle.",
        [5] = "Probe fail. Probe did not contact the workpiece within the programmed travel.",
        [6] = "Homing fail. The active homing cycle was reset.",
        [7] = "Homing fail. Safety door was opened during homing cycle.",
        [8] = "Homing fail. Pull off travel failed to clear limit switch. Try increasing pull-off setting or check wiring.",
        [9] = "Homing fail. Could not find limit switch within search distances. Try increasing max travel, decreasing pull-off distance, or check wiring.",
        [10] = "Homing fail. Second dual axis limit switch failed to trigger within configured search distance after first. Try increasing trigger fail distance or check wiring.",
    };

    public static string ErrorText(int code) => Errors.TryGetValue(code, out var text) ? $"error {code}: {text}" : $"error {code}";

    public static string AlarmText(int code) => Alarms.TryGetValue(code, out var text) ? $"alarm {code}: {text}" : $"alarm {code}";

    // "error:20" (Grbl 1.1) or "error:Bad number format" (Grbl 0.9) to a readable text.
    public static string DescribeError(string response) => Describe(response, "error:", ErrorText);

    public static string DescribeAlarm(string response) => Describe(response, "ALARM:", AlarmText);

    private static string Describe(string response, string prefix, Func<int, string> text)
    {
        var value = response.StartsWith(prefix, StringComparison.Ordinal) ? response[prefix.Length..] : response;
        return int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var code)
            ? text(code)
            : response;
    }
}
