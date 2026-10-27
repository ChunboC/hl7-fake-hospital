using Hopital.Hl7;

namespace Hopital.Tests;

public class AckTests
{
    private const string Incoming251 =
        "MSH|^~\\&|EPIC|HOPITAL|OIE|IMAGING|20261025120000||ORM^O01^ORM_O01|MSG0042|P|2.5.1\rPID|1\r";

    private const string Incoming23 =
        "MSH|^~\\&|EPIC|HOPITAL|OIE|IMAGING|20261025120000||ORM^O01|MSG0043|T|2.3\rPID|1\r";

    [Fact]
    public void AA_swaps_sender_and_receiver_and_echoes_the_control_id()
    {
        var ack = Hl7Parsed.TryParse(AckBuilder.For(Incoming251))!;

        Assert.Equal("OIE", ack.Field("MSH", 3));
        Assert.Equal("IMAGING", ack.Field("MSH", 4));
        Assert.Equal("EPIC", ack.Field("MSH", 5));
        Assert.Equal("HOPITAL", ack.Field("MSH", 6));
        Assert.Equal("ACK^O01^ACK", ack.MessageType);
        Assert.Equal("P", ack.Field("MSH", 11));
        Assert.Equal("2.5.1", ack.Version);
        Assert.Equal("AA", ack.Field("MSA", 1));
        Assert.Equal("MSG0042", ack.Field("MSA", 2));
        Assert.False(ack.HasSegment("ERR"));
    }

    [Fact]
    public void AE_in_251_has_text_in_MSA_3_and_ERR_3_and_8()
    {
        var ack = Hl7Parsed.TryParse(AckBuilder.For(Incoming251, "AE", "Database unavailable"))!;

        Assert.Equal("AE", ack.Field("MSA", 1));
        Assert.Equal("Database unavailable", ack.Field("MSA", 3));
        Assert.Equal("207^Application internal error^HL70357", ack.Field("ERR", 3));
        Assert.Equal("E", ack.Field("ERR", 4));
        Assert.Equal("Database unavailable", ack.Field("ERR", 8));
    }

    [Fact]
    public void AR_in_23_echoes_the_version_and_uses_the_old_ERR_layout()
    {
        var ack = Hl7Parsed.TryParse(AckBuilder.For(Incoming23, "AR", "Unsupported", errorCode: "200"))!;

        Assert.Equal("2.3", ack.Version);
        Assert.Equal("T", ack.Field("MSH", 11));
        Assert.Equal("AR", ack.Field("MSA", 1));
        Assert.Equal("^^^200&Unsupported message type&HL70357", ack.Field("ERR", 1));
    }

    [Fact]
    public void Message_without_MSH_gets_an_AR()
    {
        var result = AckResult.Parse(AckBuilder.For("PID|1||123\r"));

        Assert.Equal(AckStatus.Rejected, result.Status);
        Assert.Equal("", result.AckedControlId);
        Assert.Equal("No MSH segment", result.ErrorText);
    }

    [Theory]
    [InlineData("AA", AckStatus.Accepted)]
    [InlineData("CA", AckStatus.Accepted)]
    [InlineData("AE", AckStatus.Error)]
    [InlineData("CE", AckStatus.Error)]
    [InlineData("AR", AckStatus.Rejected)]
    [InlineData("CR", AckStatus.Rejected)]
    [InlineData("ZZ", AckStatus.Invalid)]
    public void Parse_maps_MSA_codes(string code, AckStatus expected)
    {
        var result = AckResult.Parse($"MSH|^~\\&|OIE|IMAGING|EPIC|HOPITAL|20261025120000||ACK|A1|P|2.5.1\rMSA|{code}|MSG0042|some text\r");

        Assert.Equal(expected, result.Status);
        Assert.Equal(code, result.Code);
        Assert.Equal("MSG0042", result.AckedControlId);
        Assert.Equal("some text", result.Text);
    }

    [Fact]
    public void Parse_round_trips_an_AE_from_the_builder()
    {
        var result = AckResult.Parse(AckBuilder.For(Incoming251, "AE", "Try later"));

        Assert.Equal(AckStatus.Error, result.Status);
        Assert.Equal("MSG0042", result.AckedControlId);
        Assert.Equal("Try later", result.ErrorText);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not hl7 at all")]
    [InlineData("MSH|^~\\&|OIE|IMAGING|EPIC|HOPITAL|20261025120000||ACK|A1|P|2.5.1\r")]
    public void Parse_reports_replies_that_are_not_ACKs_as_invalid(string reply)
    {
        Assert.Equal(AckStatus.Invalid, AckResult.Parse(reply).Status);
    }
}
