using Callsign.Core.Domain;

namespace Callsign.Core.Progression;

/// <summary>A licence class: the letter, plus self-documenting display text (shipped to the UI).</summary>
public sealed record QualClassDef(QualClass Class, string DisplayName, string Description);

/// <summary>
/// The licence classes (Phase 3c). Reference content — self-documenting so the UI never hard-codes a
/// class. Each aircraft category maps to the class needed to fly it; holding the class rates you for it.
/// </summary>
public static class QualificationClasses
{
    /// <summary>The class every career starts with — light single-engine piston.</summary>
    public static readonly QualClass Starter = QualClass.A;

    public static readonly IReadOnlyList<QualClassDef> All =
    [
        new(QualClass.A, "Class A · Single-engine piston", "Light single-engine aeroplanes — where every career starts."),
        new(QualClass.B, "Class B · Multi-engine piston", "Twin piston aeroplanes."),
        new(QualClass.C, "Class C · Turboprop",           "Turbine-prop aircraft."),
        new(QualClass.D, "Class D · Light jet",           "Entry-level business jets."),
        new(QualClass.E, "Class E · Jet",                 "Full jet aircraft."),
        new(QualClass.F, "Class F · Heavy",               "Heavy, multi-crew aircraft."),
        new(QualClass.H, "Class H · Helicopter",          "Rotary-wing aircraft."),
        new(QualClass.M, "Class M · Glider",              "Motorless and motor-glider aircraft."),
    ];

    /// <summary>The licence class an aircraft category requires. Unknown/other default to the base
    /// class, so an unrecognised airframe never locks the loop.</summary>
    public static QualClass ForCategory(AircraftCategory category) => category switch
    {
        AircraftCategory.LightSingle => QualClass.A,
        AircraftCategory.LightTwin   => QualClass.B,
        AircraftCategory.Turboprop   => QualClass.C,
        AircraftCategory.LightJet    => QualClass.D,
        AircraftCategory.Jet         => QualClass.E,
        AircraftCategory.Heavy       => QualClass.F,
        AircraftCategory.Helicopter  => QualClass.H,
        AircraftCategory.Glider      => QualClass.M,
        _                            => QualClass.A,
    };

    public static QualClassDef Def(QualClass c) => All.First(x => x.Class == c);
}
