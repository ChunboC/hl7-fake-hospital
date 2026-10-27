namespace Hopital.Generation;

public sealed record FakeAddress(string Street, string City, string State, string Zip);

public sealed record FakePatient(
    string Mrn,
    string AccountNumber,
    string LastName,
    string FirstName,
    string MiddleInitial,
    DateOnly DateOfBirth,
    char Sex,
    FakeAddress Address,
    string Phone);

public sealed record FakeProvider(string Id, string LastName, string FirstName);

public sealed record FakeProcedure(string Code, string Description);

public sealed record FakeOrder(
    string PlacerOrderNumber,
    string AccessionNumber,
    string AppointmentId,
    FakeProcedure Procedure,
    FakeProvider OrderingProvider,
    string Room,
    DateTime ScheduledAt);
