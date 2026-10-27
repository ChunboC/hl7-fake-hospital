using Hopital.Generation;

namespace Hopital.Tests;

public class FakeDataGeneratorTests
{
    private static readonly DateOnly Today = new(2026, 10, 25);
    private static readonly DateTime Slot = new(2026, 10, 25, 8, 0, 0);

    [Fact]
    public void Same_seed_gives_the_same_data()
    {
        var a = new FakeDataGenerator(42, Today);
        var b = new FakeDataGenerator(42, Today);

        Assert.Equal(a.Providers, b.Providers);
        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(a.NextPatient(), b.NextPatient());
            Assert.Equal(a.NextOrder(Slot), b.NextOrder(Slot));
        }
    }

    [Fact]
    public void Different_seeds_give_different_data()
    {
        Assert.NotEqual(new FakeDataGenerator(1, Today).NextPatient(), new FakeDataGenerator(2, Today).NextPatient());
    }

    [Fact]
    public void Identifiers_are_unique_across_a_large_run()
    {
        var generator = new FakeDataGenerator(7, Today);
        var patients = Enumerable.Range(0, 2000).Select(_ => generator.NextPatient()).ToList();
        var orders = Enumerable.Range(0, 2000).Select(_ => generator.NextOrder(Slot)).ToList();

        Assert.Equal(patients.Count, patients.Select(p => p.Mrn).Distinct().Count());
        Assert.Equal(patients.Count, patients.Select(p => p.AccountNumber).Distinct().Count());
        Assert.Equal(orders.Count, orders.Select(o => o.PlacerOrderNumber).Distinct().Count());
        Assert.Equal(orders.Count, orders.Select(o => o.AccessionNumber).Distinct().Count());
    }

    [Fact]
    public void Patients_have_well_formed_made_up_values()
    {
        var generator = new FakeDataGenerator(3, Today);

        for (var i = 0; i < 500; i++)
        {
            var p = generator.NextPatient();
            Assert.Matches(@"^9\d{7}$", p.Mrn);
            Assert.Matches(@"^8\d{9}$", p.AccountNumber);
            Assert.Matches(@"^555-01\d{2}$", p.Phone);
            Assert.Matches(@"^\d{5}$", p.Address.Zip);
            Assert.Contains(p.Sex, "FM");
            Assert.InRange(p.DateOfBirth, Today.AddYears(-91), Today.AddYears(-18));
            Assert.False(string.IsNullOrWhiteSpace(p.LastName));
            Assert.False(string.IsNullOrWhiteSpace(p.FirstName));
        }
    }

    [Fact]
    public void Orders_use_known_procedures_rooms_and_providers()
    {
        var generator = new FakeDataGenerator(5, Today);

        for (var i = 0; i < 200; i++)
        {
            var o = generator.NextOrder(Slot.AddMinutes(i));
            Assert.Matches(@"^ORD\d{8}$", o.PlacerOrderNumber);
            Assert.Matches(@"^ACC\d{8}$", o.AccessionNumber);
            Assert.Matches(@"^APT\d{8}$", o.AppointmentId);
            Assert.Contains(o.Procedure, FakeDataGenerator.Procedures);
            Assert.Contains(o.Room, FakeDataGenerator.Rooms);
            Assert.Contains(o.OrderingProvider, generator.Providers);
            Assert.Equal(Slot.AddMinutes(i), o.ScheduledAt);
        }
    }
}
