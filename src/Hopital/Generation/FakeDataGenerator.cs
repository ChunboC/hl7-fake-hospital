namespace Hopital.Generation;

/// <summary>
/// Makes up patients, providers and orders. Nothing comes from real people:
/// names are random pairs from built-in lists, identifiers are random numbers,
/// phones use the 555-0100 to 555-0199 range reserved for fiction, and no SSN is ever produced.
/// The same seed always gives the same data, so a scenario run can be repeated exactly.
/// </summary>
public sealed class FakeDataGenerator
{
    private static readonly string[] FemaleFirstNames =
    [
        "Avery", "Brooke", "Carmen", "Dana", "Elena", "Fiona", "Grace", "Hannah", "Iris", "Julia",
        "Keiko", "Laura", "Maya", "Nadia", "Olivia", "Priya", "Quinn", "Rosa", "Sofia", "Tessa",
    ];

    private static readonly string[] MaleFirstNames =
    [
        "Aaron", "Ben", "Carlos", "David", "Ethan", "Felix", "George", "Hiro", "Isaac", "Jamal",
        "Kevin", "Luis", "Marcus", "Nolan", "Omar", "Paul", "Raj", "Samuel", "Tomas", "Victor",
    ];

    private static readonly string[] LastNames =
    [
        "Abbott", "Barnes", "Castillo", "Dawson", "Ellison", "Fischer", "Garza", "Holloway", "Ibarra", "Jensen",
        "Kowalski", "Lindqvist", "Moreno", "Nakamura", "Okafor", "Patel", "Quintero", "Reyes", "Sorensen", "Tanaka",
        "Underwood", "Vasquez", "Whitaker", "Xiong", "Yamada", "Zimmerman", "Brennan", "Chandra", "Delgado", "Fontaine",
    ];

    private static readonly string[] Streets =
    [
        "Maple Ave", "Oak St", "Cedar Ln", "Birch Rd", "Elm St", "Willow Way", "Spruce Ct", "Aspen Dr", "Pine St", "Juniper Pl",
    ];

    private static readonly (string City, string State, string ZipPrefix)[] Cities =
    [
        ("Springfield", "IL", "627"), ("Riverton", "WY", "825"), ("Fairview", "OR", "970"), ("Lakewood", "CO", "802"),
        ("Greenville", "SC", "296"), ("Madison", "WI", "537"), ("Franklin", "TN", "370"), ("Clinton", "IA", "527"),
    ];

    /// <summary>Local codes, not CPT, so no licensed code set is involved.</summary>
    public static readonly IReadOnlyList<FakeProcedure> Procedures =
    [
        new("ENDO-EGD", "Upper GI endoscopy (EGD)"),
        new("ENDO-COLO", "Colonoscopy"),
        new("ENDO-SIG", "Flexible sigmoidoscopy"),
        new("ENDO-ERCP", "ERCP"),
        new("ENDO-EUS", "Endoscopic ultrasound"),
        new("ENDO-BRONCH", "Bronchoscopy"),
    ];

    public static readonly IReadOnlyList<string> Rooms = ["ENDO1", "ENDO2", "ENDO3", "ENDO4"];

    private readonly Random _random;
    private readonly DateOnly _today;
    private readonly HashSet<string> _usedIds = [];

    public FakeDataGenerator(int seed, DateOnly today, int providerCount = 6)
    {
        _random = new Random(seed);
        _today = today;
        Providers = Enumerable.Range(0, providerCount)
            .Select(_ => new FakeProvider(UniqueNumber("PRV", 6), Pick(LastNames), Pick(_random.Next(2) == 0 ? FemaleFirstNames : MaleFirstNames)))
            .ToList();
    }

    public IReadOnlyList<FakeProvider> Providers { get; }

    public FakePatient NextPatient()
    {
        var sex = _random.Next(2) == 0 ? 'F' : 'M';
        var (city, state, zipPrefix) = Pick(Cities);
        var age = _random.Next(18, 91);
        var dateOfBirth = _today.AddYears(-age).AddDays(-_random.Next(0, 365));

        return new FakePatient(
            Mrn: UniqueNumber("9", 7),
            AccountNumber: UniqueNumber("8", 9),
            LastName: Pick(LastNames),
            FirstName: Pick(sex == 'F' ? FemaleFirstNames : MaleFirstNames),
            MiddleInitial: ((char)('A' + _random.Next(26))).ToString(),
            DateOfBirth: dateOfBirth,
            Sex: sex,
            Address: new FakeAddress($"{_random.Next(100, 9900)} {Pick(Streets)}", city, state, zipPrefix + _random.Next(0, 100).ToString("00")),
            Phone: $"555-01{_random.Next(0, 100):00}");
    }

    public FakeOrder NextOrder(DateTime scheduledAt) =>
        new(
            PlacerOrderNumber: UniqueNumber("ORD", 8),
            AccessionNumber: UniqueNumber("ACC", 8),
            AppointmentId: UniqueNumber("APT", 8),
            Procedure: Pick(Procedures),
            OrderingProvider: Pick(Providers),
            Room: Pick(Rooms),
            ScheduledAt: scheduledAt);

    private T Pick<T>(IReadOnlyList<T> items) => items[_random.Next(items.Count)];

    /// <summary>A prefix plus random digits, never repeated within this generator.</summary>
    private string UniqueNumber(string prefix, int digits)
    {
        var max = (int)Math.Pow(10, digits);
        while (true)
        {
            var id = prefix + _random.Next(0, max).ToString(new string('0', digits));
            if (_usedIds.Add(id))
                return id;
        }
    }
}
