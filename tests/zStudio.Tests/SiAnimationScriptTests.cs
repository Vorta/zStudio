using System.Text;
using Recoil.Zbd.Core.Animation;
using Xunit;

namespace Recoil.Zbd.Tests;

/// <summary>
/// SI Animation Scripts. The expected bits come from the reference model of the lost compiler that reproduces every
/// keyframe of the shipped 1999 <c>anim.zbd</c> files; the inputs here are synthetic.
/// </summary>
public sealed class SiAnimationScriptTests
{
    // α, β, γ in millionths of a radian, then the expected stored quaternion bits (W X Y Z): exact ±90° headings,
    // whole turns, past-90° headings and signed zeros included.
    public static readonly TheoryData<long, long, long, uint, uint, uint, uint> Rotations = new()
    {
        { 0, 0, 0, 0x3F800000, 0x00000000, 0x00000000, 0x00000000 },
        { 0, 1570796, 0, 0x3F3504F5, 0x00000000, 0x3F3504F1, 0x00000000 },
        { -75903, -1570796, -50889, 0x3F34A7DD, 0xBD377DD1, 0xBF34A7D9, 0xBD377DD1 },
        { 45632, 1570796, 6283710, 0x3F34F92C, 0x3C82A237, 0x3F34F928, 0xBC82A234 },
        { -6196465, 1570796, -7990, 0x3F34D102, 0x3D091A9A, 0x3F34D0FE, 0xBD091A97 },
        { 0, 3870744, 0, 0x3EB68E1A, 0x00000000, 0xBF6F2CA8, 0x00000000 },
        { 13891, 1571142, -14374, 0x3F34F850, 0x3C23B932, 0x3F350855, 0xBC23B8F3 },
        { 1236189, 1559180, 1199628, 0x3F355973, 0x3C897493, 0x3F349F68, 0xBC14A06E },
        { 304886, -111792, -811317, 0x3F68FD63, 0x3DF0AEA4, 0xBDE271F6, 0xBEC366BF },
        { -36360, -3141593, 0, 0x34510216, 0xB123D885, 0x3F7FF52C, 0x3C94EC1F },
        { 3141593, 0, -3141593, 0xB33BBD35, 0xB42EEF4C, 0xBF800000, 0x342EEF4B },
        { 0, -8695273, 0, 0x3EB6A3B9, 0x00000000, 0xBF6F2887, 0x00000000 },
        { -5362863, -5007546, 928711, 0x3F43146E, 0x3DA3FD51, 0x3F230537, 0x3DB00171 },
        { 5917512, 1718832, 6583911, 0x3F1D3735, 0xBE6A6F8C, 0x3F37F972, 0x3E6DE9AD },
        { -4596004, 4110348, 4699332, 0x3F301ECA, 0xBE328661, 0xBF292D85, 0x3E79F251 },
        { 2042981, -6335898, -1462475, 0xBECE96F4, 0xBF201CA1, 0x3F146049, 0x3EA9E19B },
        { 1669351, -4941920, -3069130, 0x3EE1E664, 0xBEE0459B, 0x3F10A97A, 0x3F0AD736 },
        { 2927599, -1884018, 210968, 0xBCB5E03A, 0x3F173472, 0xBCC7301E, 0x3F4E6713 },
        { 5177679, 5109435, -6109615, 0x3F3B307C, 0xBECA1442, 0xBF01E60B, 0xBE69B35B },
        { 362337, -6177319, 1075732, 0x3F5933F6, 0x3E02EDC5, 0x3E0C280E, 0x3EFD7435 },
        { -3239710, 374165, -2765283, 0x3E31A444, 0xBE452463, 0x3F765C17, 0x3DA819CE },
        { 4788045, -4690427, -6285738, 0x3F037136, 0xBEF31327, 0x3F065AAB, 0x3EF87CE0 },
        { 3694616, -333442, 3017002, 0x3E345294, 0xBC62CE13, 0xBF731C95, 0x3E847B90 },
        { -5365329, 3121330, -1849922, 0x3EB25242, 0xBF37F814, 0xBF0935D4, 0x3E8C373D },
        { 209426, 6834418, 5182633, 0x3F4D0403, 0x3E68B66F, 0x3E36606E, 0xBF065127 },
        { -5032016, -662069, -3284886, 0xBE0A3231, 0x3E9AD8DD, 0x3F08934B, 0x3F473C84 },
        { -5907883, 6086672, 2928705, 0x3DAF77A9, 0x3DECA84A, 0x3E328BE7, 0x3F795FBD },
        { 4544275, 1718595, 5838478, 0x3F09DE9E, 0xBEC1F26E, 0x3F163061, 0x3EF1640E },
        { 2332519, -4051844, -5923722, 0xBEA2BB41, 0xBEAB34C6, 0xBED7138C, 0x3F480EE5 },
        { 6431498, 760350, 4903396, 0x3F325FD2, 0x3E93C298, 0x3E777265, 0xBF1C4FE6 },
        { 1169676, 1208456, -2989555, 0x3E8570A9, 0xBF01C04B, 0x3ED584BE, 0x3F353FFC },
        { 4788626, 3655548, 837942, 0x3EE01D81, 0x3E05F65F, 0xBF37E6B8, 0xBF064E0C },
        { 2267030, 3748558, -3162491, 0x3F5CF69F, 0xBED05978, 0xBE887130, 0xBE0AD5BA },
        { -4922660, 1807212, 4320936, 0x3E129ACE, 0xBF39586A, 0xBC8411B4, 0x3F2CB151 },
        { -64750, -1826035, -4082670, 0x3E9986DB, 0x3F322DB3, 0xBEC0A058, 0x3F08741C },
        { -6637441, -20150, 5253894, 0x3F5B1E40, 0xBE220D05, 0x3D9FF033, 0xBEF8DAFD },
        { -5524884, -6723937, 5541015, 0x3F5FC84B, 0x3E86A247, 0xBEA3F443, 0xBE81B39D },
        { -4636075, -3542576, -2359767, 0x3F1CF22D, 0xBF2BE2A5, 0xBDF318A7, 0x3ECC504B },
        { -5589500, 2119093, 3020312, 0x3EA5C9C8, 0xBF4F012E, 0x3E5CE521, 0x3EE1F4A8 },
        { -4181834, 4639192, 3646146, 0x3F333127, 0xBE51FD71, 0xBF29B433, 0xBE2D0308 },
        { -1995104, -6490098, -446869, 0xBF01BF57, 0x3F53C968, 0xBE05ADD9, 0x3E510A2E },
        { 6543192, 4723181, -625114, 0x3F32E436, 0xBE01C661, 0xBF31204C, 0xBE051F0F },
        { 73549, -4744111, 6383707, 0x3F37D71C, 0xBC0C3F01, 0x3F321EB5, 0x3C2C30CB },
        { 576191, -6800711, -375102, 0x3F6C964B, 0x3E657C2A, 0xBE95A1B6, 0xBDCFA4CF },
        { -4548195, -1833327, -1327375, 0x3F2ECF34, 0x3D4A5F41, 0xBF30AFA1, 0x3E6FFDCF },
        { 4824203, 1107626, -6123108, 0x3F1AA134, 0xBF18ADBE, 0x3EB0E714, 0x3ECCDA7E },
        { -5795932, 23427, -100900, 0x3F781213, 0x3E7744DF, 0xBA5490C8, 0xBD54002E },
        { 2082370, -3387478, -553593, 0x3E32A618, 0xBE748D69, 0xBEE82136, 0x3F573FE3 },
        { -519821, 2822891, -1563653, 0x3E934006, 0x3F24B268, 0x3F34B1BA, 0x3D937C3D },
        { -3108153, 2349449, -6604689, 0x3E1DB639, 0xBEC1B0B1, 0x3D9DA173, 0x3F68D6E8 },
        { -5079616, 2491810, -4373666, 0x3F1704A0, 0xBF087EC6, 0x3F194F9D, 0xBDC308B8 },
        { -3973060, 970910, 2868344, 0x3EF16D69, 0xBD9CA57A, 0x3F53C6B6, 0x3E976A47 },
        { -393557, -3429177, 2345242, 0xBDFDADF7, 0xBF67CFD1, 0x3EB37577, 0x3E5178D6 },
        { -2850826, 6663947, -4563156, 0xBD490FC0, 0xBF27978C, 0xBF37EA90, 0x3E6B7C21 },
        { 4303133, -3036290, -1941149, 0x3F2C3262, 0x3EF43818, 0x3E8BDC33, 0x3EFDA3CC },
        { -1089838, 4034716, -580250, 0xBE616CA6, 0x3EDECC47, 0x3F2CCF1B, 0x3F0DBACD },
        { -4081662, 5386494, 435316, 0x3EA150AC, 0x3F53AE23, 0xBC9516E7, 0x3EEE5426 },
        { 1295872, 3169722, -782578, 0x3E764B90, 0xBE97A8C9, 0xBF3D7FD7, 0x3F0DB84B },
        { 3029573, -410566, 2004703, 0x3E116C78, 0xBF092ACF, 0xBF514EBD, 0xBE1F7C75 },
        { 1640123, -3869044, 2493623, 0x3F399D82, 0xBF058576, 0x3EE62BF1, 0x3C4D25E6 },
    };

    [Theory, MemberData(nameof(Rotations))]
    public void RotationsCompileAsTheShippedFilesStoreThem(long a, long b, long g, uint w, uint x, uint y, uint z)
    {
        var q = SiMath.CompileRotation(a / 1e6, b / 1e6, g / 1e6);
        Assert.Equal((w, x, y, z), (SiMath.Bits(q.W), SiMath.Bits(q.X), SiMath.Bits(q.Y), SiMath.Bits(q.Z)));
    }

    // From and to angles (millionths of a radian), frames, frame rate, and the expected spin bits.
    public static readonly TheoryData<long[], long[], int, int, float, uint[]> Spins = new()
    {
        { [-4081662, 5386494, 435316], [-4115803, 5238562, 682025], 1278, 1279, 10f, [0x3E9F2247, 0xBF2DFB08, 0x3F8B8D2D] },
        { [3029573, -410566, 2004703], [3136736, -467773, 1879250], 1012, 1037, 10f, [0x3B6CF0F3, 0x3CB61B4E, 0xBC82A90A] },
        { [-519821, 2822891, -1563653], [-416187, 3104927, -1687858], 1542, 1567, 16f, [0x3DBC1829, 0x3CE352BD, 0xBD3987AE] },
        { [4824203, 1107626, -6123108], [4795789, 1148216, -6354675], 408, 417, 15f, [0xBC3EF2C8, 0x3D08C87B, 0xBE2FA50E] },
        { [3694616, -333442, 3017002], [3765949, -116814, 2834010], 1390, 1391, 16f, [0xBF6AB1D8, 0xBFC97E2D, 0xBFAA38AE] },
        { [-5079616, 2491810, -4373666], [-5013623, 2630445, -4144594], 1404, 1414, 15f, [0xBD9A7471, 0xBDA9BAFE, 0x3E13C865] },
        { [-5032016, -662069, -3284886], [-5104931, -769966, -3349493], 1329, 1339, 30f, [0x3DE067DE, 0x3E1460F0, 0xBE2CA238] },
        { [0, -8695273, 0], [132408, -8591360, 193354], 1862, 1887, 15f, [0xBCFF4B97, 0x3CE819FF, 0x3DAFD69B] },
        { [-4922660, 1807212, 4320936], [-4885224, 1831558, 4380797], 1335, 1340, 30f, [0x3D9FE8C2, 0x378319A3, 0x3D90D27A] },
        { [-5907883, 6086672, 2928705], [-6188705, 5820123, 2881830], 1176, 1177, 15f, [0x401A104B, 0x3FBB2EF4, 0xBF83EEC4] },
        { [5177679, 5109435, -6109615], [5051818, 5067368, -5821934], 1908, 1917, 20f, [0xBD0C3985, 0xBD77636E, 0x3E41F23F] },
        { [0, 3870744, 0], [233969, 4013802, -208335], 1431, 1456, 15f, [0xBD34D674, 0x3D42A312, 0xBC41CA21] },
        { [6543192, 4723181, -625114], [6397173, 4444080, -849150], 1941, 1966, 16f, [0xBD6443DD, 0xBD9024D9, 0xBDF10B02] },
        { [4788045, -4690427, -6285738], [4627305, -4779142, -6156241], 293, 302, 30f, [0x3B50E8A3, 0xBE177F39, 0x3EF789B9] },
        { [0, 1570796, 0], [200899, 1273313, 8418], 1381, 1386, 20f, [0x3D7EA842, 0xBF178537, 0xBEC365D3] },
        { [1640123, -3869044, 2493623], [1533931, -3628428, 2738307], 363, 372, 20f, [0xBE5FF209, 0xBE3B3CFB, 0x3EACDB8C] },
    };

    [Theory, MemberData(nameof(Spins))]
    public void SpinsUseTheEnginesFastSquareRootAndWideEndTime(long[] from, long[] to, int f0, int f1, float rate, uint[] expected)
    {
        var q0 = SiMath.CompileRotation(from[0] / 1e6, from[1] / 1e6, from[2] / 1e6);
        var q1 = SiMath.CompileRotation(to[0] / 1e6, to[1] / 1e6, to[2] / 1e6);
        var spin = SiMath.Spin(q0, q1, f0, f1, rate);
        Assert.Equal(expected, new[] { SiMath.Bits(spin.X), SiMath.Bits(spin.Y), SiMath.Bits(spin.Z) });
    }

    [Theory]
    [InlineData("2541.143500", "2543.123347", 2491, 2496, 15f, 0x40BE0ED0u, 0x43266667u)]
    [InlineData("1241.240293", "1253.482586", 603, 604, 40f, 0x43F4D796u, 0x4171999Au)]
    [InlineData("-2478.598490", "-2517.668573", 378, 388, 20f, 0xC29C47B3u, 0x419B3333u)]
    [InlineData("2206.353250", "2235.286088", 1861, 1871, 15f, 0x422D98E2u, 0x42F97778u)]
    [InlineData("-1093.997841", "-1087.437096", 123, 128, 15f, 0x419D74CAu, 0x41088889u)]
    [InlineData("-1705.449563", "-1738.429105", 2616, 2617, 20f, 0xC424E6AEu, 0x4302D99Au)]
    [InlineData("-177.586523", "-190.132153", 2278, 2293, 15f, 0xC148BAA2u, 0x4318DDDEu)]
    [InlineData("-37.288728", "-7.571907", 426, 431, 20f, 0x42EDBC40u, 0x41AC6667u)]
    [InlineData("-647.510046", "-669.487284", 846, 851, 40f, 0xC32FD11Fu, 0x41AA3333u)]
    [InlineData("-788.834183", "-752.825600", 1845, 1850, 40f, 0x4390088Cu, 0x42390000u)]
    [InlineData("2041.090029", "2090.447501", 1800, 1801, 30f, 0x44B91819u, 0x42702223u)]
    [InlineData("-664.884428", "-636.469647", 769, 779, 20f, 0x42635165u, 0x421BCCCDu)]
    public void VelocitiesAndTimesMatchTheShippedArithmetic(string from, string to, int f0, int f1, float rate, uint velocity, uint time)
    {
        float a = (float)double.Parse(from, System.Globalization.CultureInfo.InvariantCulture), b = (float)double.Parse(to, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(velocity, SiMath.Bits(SiMath.ComponentRate(a, b, SiMath.InverseDuration(f0, f1, rate))));
        Assert.Equal(time, SiMath.Bits(SiMath.KeyTime(f1, rate)));
    }

    private const string Gate = """
        SI Animation Script
        FRAMES: 4
        OBJECTS: 2
        Warning, file version 3.7 is later than DKit release version 3
        Attempt to read: An error may occur...
        Frame: 1
        Object: door
        Scaling:     1.000000 1.000000 1.000000
        Rotation:    0.000000 0.000000 0.000000
        Translation: 0.000000 0.000000 0.000000
        Object: lamp
        Scaling:     1.000000 1.000000 1.000000
        Rotation:    0.000000 1.570796 0.000000
        Translation: 2.000000 0.000000 0.000000
        Warning, file version 3.7 is later than DKit release version 3
        Attempt to read: An error may occur...
        Frame: 11
        Object: door
        Scaling:     1.000000 1.000000 1.000000
        Rotation:    0.000000 0.500000 0.000000
        Translation: 0.000000 4.000000 0.000000
        Object: lamp
        Scaling:     1.000000 1.000000 1.000000
        Rotation:    0.000000 1.570796 0.000000
        Translation: 2.000000 0.000000 0.000000
        Warning, file version 3.7 is later than DKit release version 3
        Attempt to read: An error may occur...
        Frame: 21
        Object: door
        Scaling:     1.000000 1.000000 1.000000
        Rotation:    0.000000 0.500000 0.000000
        Translation: 0.000000 4.000000 0.000000
        Object: lamp
        Scaling:     1.000000 1.000000 1.000000
        Rotation:    0.000000 1.570796 0.000000
        Translation: 2.000000 0.000000 0.000000
        Warning, file version 3.7 is later than DKit release version 3
        Attempt to read: An error may occur...
        Frame: 31
        Object: door
        Scaling:     1.000000 1.000000 1.000000
        Rotation:    0.000000 0.000000 0.000000
        Translation: 0.000000 0.000000 0.000000
        Object: lamp
        Scaling:     1.000000 1.000000 1.000000
        Rotation:    0.000000 1.570796 0.000000
        Translation: 2.000000 0.000000 0.000000
        """;

    [Fact]
    public void ScriptsCompileChangedChannelsAndLeaveStillSegmentsOut()
    {
        Assert.True(SiAnimationScript.Recognize(Encoding.ASCII.GetBytes("\r\n" + Gate)));
        Assert.False(SiAnimationScript.Recognize("OBJECT door\nFRAME 0 POSITION 0 0 0\nFRAME 1"u8));
        var script = SiAnimationScript.Parse(Encoding.ASCII.GetBytes(Gate.Replace("\n", "\r\n")), "gate.zan");
        Assert.Equal(["door", "lamp"], script.Objects);
        var door = SiAnimationScript.Compile(script, "door", 10, "gate.zan");
        // Frames 0-10 move everything (the first segment carries every channel), 10-20 holds (left out), 20-30 is the
        // last segment and carries every channel again.
        Assert.Equal([(7, 0f, 1f), (7, 2f, 3f)], door.Select(f => (f.Flags, f.Start, f.End)));
        Assert.Equal(4f, door[0].F32(door[0].ChannelOffset(0) + 20));
        var lamp = SiAnimationScript.Compile(script, "lamp", 10, "gate.zan");
        Assert.Equal([7, 7], lamp.Select(f => f.Flags));
        Assert.All(lamp, f => Assert.Equal(0f, f.F32(f.ChannelOffset(1) + 16)));
    }

    [Theory]
    [InlineData("Frame: 1\nObject: a\nScaling: 1 1 1\nRotation: 0 0 0\nFrame: 2", "needs Scaling, Rotation and Translation")]
    [InlineData("Frame: 0\nObject: a\nScaling: 1 1 1\nRotation: 0 0 0\nTranslation: 0 0 0", "frame label")]
    [InlineData("Object: a", "belongs to a frame")]
    [InlineData("Frame: 1\nObject: a\nScaling: 1 1\nRotation: 0 0 0\nTranslation: 0 0 0", "three finite numbers")]
    [InlineData("Frame: 1\nObject: a\nScaling: 1 1 1e40\nRotation: 0 0 0\nTranslation: 0 0 0", "three finite numbers")]
    [InlineData("Frame: 1\nObject: a\nScaling: 1 1 1\nScaling: 1 1 1", "given twice")]
    [InlineData("Frame: 1\nObject: a\nScaling: 1 1 1\nRotation: 0 0 0\nTranslation: 0 0 0\nObject: a", "appears twice")]
    [InlineData("Frame: 1\nRotation: 0 0 0", "belongs to an object")]
    [InlineData("Frame: 1\nPosition: 0 0 0", "unknown line")]
    [InlineData("Frame: 1\nObject: a\nScaling: 1 1 1\nRotation: 0 0 0\nTranslation: 0 0 0\nFRAMES: 2", "header")]
    public void MalformedScriptsAreRefusedWithTheirLine(string text, string message)
    {
        var error = Assert.Throws<InvalidDataException>(() => SiAnimationScript.Parse(Encoding.ASCII.GetBytes(SiAnimationScript.Header + "\n" + text), "bad.zan"));
        Assert.Contains(message, error.Message);
        Assert.StartsWith("bad.zan, line ", error.Message);
    }

    [Fact]
    public void AnObjectNeedsTwoFramesAndAFrameRate()
    {
        var one = SiAnimationScript.Parse(Encoding.ASCII.GetBytes(SiAnimationScript.Header + "\nFrame: 1\nObject: a\nScaling: 1 1 1\nRotation: 0 0 0\nTranslation: 0 0 0\nFrame: 1\nObject: a\nScaling: 1 1 1\nRotation: 0 0 0\nTranslation: 1 0 0"), "one.zan");
        Assert.Contains("at least two frames", Assert.Throws<InvalidDataException>(() => SiAnimationScript.Compile(one, "a", 10, "one.zan")).Message);
        Assert.Contains("SCRIPT_FRAME_RATE", Assert.Throws<InvalidDataException>(() => SiAnimationScript.Compile(one, "a", 0, "one.zan")).Message);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A script of unit-scale objects: per frame label, each object's rotation and translation.</summary>
    private static string Script(params (int Label, (string Object, double[] Rotation, double[] Translation)[] Poses)[] frames)
    {
        StringBuilder text = new(SiAnimationScript.Header + "\n");
        foreach (var (label, poses) in frames)
        {
            text.Append("Frame: ").Append(label).Append('\n');
            foreach (var (name, r, t) in poses)
                text.Append("Object: ").Append(name).Append("\nScaling:     1.000000 1.000000 1.000000\nRotation:    ").Append(Numbers(r)).Append("\nTranslation: ").Append(Numbers(t)).Append('\n');
        }
        return text.ToString();
        static string Numbers(double[] v) => string.Join(" ", v.Select(x => x.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)));
    }

    // A body that moves, holds while a rotor turns, moves on and holds while everything stands still, and a rotor whose
    // heading passes exactly 90 degrees with a whole turn in its third angle.
    private static readonly string Flight = Script(
        (1, [("body", [0, 0, 0], [0, 0, 0]), ("rotor", [0, 1.2, 0], [0, 2, 0])]),
        (6, [("body", [0.1, 0.2, 0.3], [1.5, 0, -2.25]), ("rotor", [0.01, 1.4, 0.02], [0, 2, 0])]),
        (11, [("body", [0.2, 0.25, 0.5], [3, 0.5, -4]), ("rotor", [0.03, 1.55, 0.04], [0, 2, 0])]),
        (16, [("body", [0.2, 0.25, 0.5], [3, 0.5, -4]), ("rotor", [0.045632, 1.570796, 6.28371], [0, 2, 0])]),
        (21, [("body", [0.2, 0.25, 0.5], [3, 0.5, -4]), ("rotor", [0.06, 1.5, 6.3], [0, 2, 0])]),
        (26, [("body", [0.3, 0.25, 0.6], [4, 0.5, -5]), ("rotor", [0.1, 1.2, 6.35], [0, 2, 0])]),
        (31, [("body", [0.35, 0.3, 0.65], [4.5, 0.75, -5.5]), ("rotor", [0.12, 1, 6.4], [0, 2, 0])]),
        (36, [("body", [0.35, 0.3, 0.65], [4.5, 0.75, -5.5]), ("rotor", [0.12, 1, 6.4], [0, 2, 0])]),
        (41, [("body", [0.35, 0.3, 0.65], [4.5, 0.75, -5.5]), ("rotor", [0.12, 1, 6.4], [0, 2, 0])]),
        (46, [("body", [0.35, 0.3, 0.65], [4.5, 0.75, -5.5]), ("rotor", [0.12, 1, 6.4], [0, 2, 0])]),
        (51, [("body", [0.4, 0.3, 0.7], [5, 1, -6]), ("rotor", [0.15, 0.9, 6.5], [0, 2, 0])]));

    private static List<SiScriptWriter.Track> Tracks(string text, float rate)
    {
        var script = SiAnimationScript.Parse(Encoding.ASCII.GetBytes(text), "flight.zan");
        return [.. script.Objects.Select(o => new SiScriptWriter.Track(o, SiAnimationScript.Compile(script, o, rate, "flight.zan"), rate))];
    }

    [Fact]
    public void CompiledKeyframesAreWrittenBackAsTheScriptTheyCameFrom()
    {
        var tracks = Tracks(Flight, 15);
        string written = SiScriptWriter.Write(tracks, new("3.7", true), Token);
        var again = SiAnimationScript.Parse(Encoding.Latin1.GetBytes(written), "again.zan");
        foreach (var track in tracks)
        {
            var compiled = SiAnimationScript.Compile(again, track.Object, 15, "again.zan");
            Assert.Equal(track.Frames.Count, compiled.Count);
            Assert.All(compiled.Zip(track.Frames), p => Assert.True(SiScriptWriter.Same(p.First, p.Second)));
        }
        // The original layout: header, the DKit messages before every frame, every frame the exporter wrote (the still
        // frames 36 and 41 return on the script's five-frame step), no repeated last frame and no trailing blank line.
        var lines = written.Split("\r\n");
        Assert.Equal(["SI Animation Script", "FRAMES: 11", "OBJECTS: 2", "Warning, file version 3.7 is later than DKit release version 3", "Attempt to read: An error may occur...", "Frame: 1"], lines.Take(6));
        Assert.Equal(["1", "6", "11", "16", "21", "26", "31", "36", "41", "46", "51"], lines.Where(l => l.StartsWith("Frame: ")).Select(l => l[7..]));
        Assert.Equal(11, lines.Count(l => l.StartsWith("Warning, ")));
        Assert.EndsWith("Translation: 0.000000 2.000000 0.000000\r\n", written);
        // Keyed values are the ones the floats came from; a held body keeps its pose; the 90-degree key compiles exactly.
        Assert.Contains("Translation: 1.500000 0.000000 -2.250000", written);
        Assert.Equal(3, lines.Count(l => l == "Translation: 3.000000 0.500000 -4.000000"));
    }

    [Fact]
    public void MessagesFollowFramesWhenTheLayoutSaysSo()
    {
        string written = SiScriptWriter.Write(Tracks(Flight, 15), new("3.71", false), Token);
        Assert.Contains("\r\nFrame: 1\r\nObject: body\r\n", written);
        Assert.EndsWith("Warning, file version 3.71 is later than DKit release version 3\r\nAttempt to read: An error may occur...\r\n", written);
        // Without a version (no shipped file behind the script) there are no messages at all.
        string plain = SiScriptWriter.Write(Tracks(Flight, 15), new(null), Token);
        Assert.StartsWith("SI Animation Script\r\nFRAMES: 11\r\nOBJECTS: 2\r\nFrame: 1\r\n", plain);
        Assert.DoesNotContain("Warning", plain);
    }

    [Fact]
    public void SearchesAndScriptsStayBounded()
    {
        // The rotation search stops when its budget is spent; the keyframes then keep zStudio's format.
        var error = Assert.Throws<InvalidDataException>(() => SiScriptWriter.Write(Tracks(Flight, 15), new("3.7"), Token, evaluations: 1_000));
        Assert.Contains("took too long", error.Message);
        // A still stretch is written on the script's step, but never beyond the frames a script may hold.
        var far = AnimationScript.Compile(AnimationScript.Track(AnimationScript.Parse(
            "FRAME 0 POSITION 0 0 0 ROTATION 1 0 0 0 SCALE 1 1 1\nFRAME 1 POSITION 1 0 0 ROTATION 1 0 0 0 SCALE 1 1 1\nFRAME 2\nFRAME 200000 POSITION 1 0 0 ROTATION 1 0 0 0 SCALE 1 1 1\nFRAME 200001"u8, "far.zan"), "a")!, 10, "far.zan");
        Assert.Contains($"more than {SiAnimationScript.MaximumFrames} frames", Assert.Throws<InvalidDataException>(() => SiScriptWriter.Write([new("a", far, 10)], new("3.7"), Token)).Message);
    }

    [Fact]
    public void KeyframesWithoutAnExactScriptAreRefused()
    {
        // Off the frame grid of the given rate.
        Assert.Contains("frame grid", Assert.Throws<InvalidDataException>(() => SiScriptWriter.Write([.. Tracks(Flight, 15).Select(t => t with { FrameRate = 16 })], new("3.7", true), Token)).Message);
        // A cut (two keys at one frame) and a value no six-decimal text reads back as.
        var cut = AnimationScript.Compile(AnimationScript.Track(AnimationScript.Parse("FRAME 0 POSITION 0 0 0 ROTATION 1 0 0 0 SCALE 1 1 1\nFRAME 0 POSITION 5 5 5 ROTATION 0 1 0 0 SCALE 1 1 1\nFRAME 10 POSITION 5 5 5\nFRAME 20"u8, "cut.zan"), "a")!, 10, "cut.zan");
        Assert.Contains("two keys at frame 0", Assert.Throws<InvalidDataException>(() => SiScriptWriter.Write([new("a", cut, 10)], new("3.7", true), Token)).Message);
        var fine = AnimationScript.Compile(AnimationScript.Track(AnimationScript.Parse("FRAME 0 POSITION 0.1234567 0 0 ROTATION 1 0 0 0 SCALE 1 1 1\nFRAME 10 POSITION 1 0 0 ROTATION 1 0 0 0 SCALE 1 1 1\nFRAME 20"u8, "fine.zan"), "a")!, 10, "fine.zan");
        Assert.Contains("six-decimal", Assert.Throws<InvalidDataException>(() => SiScriptWriter.Write([new("a", fine, 10)], new("3.7", true), Token)).Message);
    }
}
