using CalCrony.Bot.GameNights;
using CalCrony.Contracts;
using CalCrony.Contracts.GameNights;

namespace CalCrony.Bot.Tests.GameNights;

public class MemberNameSyncTests
{
    [Fact]
    public void A_server_nickname_in_the_convention_is_turned_into_first_l_and_nothing_else_is_sent()
    {
        var dto = MemberNameSyncService.ToDto(5, present: true, nickname: "alex pearson", globalName: "Totally Different");

        Assert.Equal(new MemberNameDto(5, "Alex P"), dto);
    }

    [Fact]
    public void With_no_nickname_the_chosen_global_name_is_used_if_it_fits_the_convention()
    {
        Assert.Equal("Bea W", MemberNameSyncService.ToDto(5, true, null, "Bea West").DisplayName);
        Assert.Null(MemberNameSyncService.ToDto(5, true, null, "xX_Bea_Xx").DisplayName);
        Assert.Null(MemberNameSyncService.ToDto(5, true, "", null).DisplayName);
    }

    [Fact]
    public void A_nickname_that_does_not_fit_is_not_overridden_by_the_global_name()
    {
        // They chose a server nickname; if it isn't First L the answer is "no match" so the member is nudged.
        Assert.Null(MemberNameSyncService.ToDto(5, true, "Alex | Bristol", "Alex Pearson").DisplayName);
        Assert.Null(MemberNameSyncService.ToDto(5, true, "Alex", "Alex Pearson").DisplayName);
    }

    [Fact]
    public void A_member_who_is_gone_is_reported_as_having_left_with_no_name()
    {
        var dto = MemberNameSyncService.ToDto(5, present: false, nickname: "Alex P", globalName: null);

        Assert.True(dto.Left);
        Assert.Null(dto.DisplayName);
    }

    [Fact]
    public void Whatever_the_bot_sends_the_api_accepts_it_is_always_exactly_the_first_l_form()
    {
        foreach (var nick in new[] { "Alex Pearson", "ALEX PEARSON", "Mary-Jane Smith", "Anne de Smith", "Alex P." })
        {
            var sent = MemberNameSyncService.ToDto(1, true, nick, null).DisplayName;
            Assert.Equal(sent, DisplayNames.FromNickname(sent)); // the API's own check passes
        }
    }
}

public class ResultTextTests
{
    [Fact]
    public void A_dispute_names_the_game_the_rejector_and_the_organiser_roles()
    {
        var payload = new ResultDisputedPayload(Guid.NewGuid(), Guid.NewGuid(), 1, 77, [900, 901]);

        var text = ResultText.Disputed(payload, "Root", 2);

        Assert.Contains("<@77>", text);
        Assert.Contains("Root, table 2", text);
        Assert.Contains("<@&900> <@&901>", text);
        Assert.Contains("settle it", text);
    }

    [Fact]
    public void With_no_organiser_roles_mapped_the_notice_still_reads_properly()
    {
        var text = ResultText.Disputed(new ResultDisputedPayload(Guid.NewGuid(), Guid.NewGuid(), 1, 77, []), "Arcs", 1);

        Assert.Contains("Organisers: please have a look", text);
        Assert.DoesNotContain("<@&", text);
    }

    [Fact]
    public void The_result_handler_owns_only_the_dispute_notice()
    {
        var handler = new ResultDeliveryHandler(null!, null!, Microsoft.Extensions.Logging.Abstractions.NullLogger<ResultDeliveryHandler>.Instance);

        Assert.True(handler.Handles(DeliveryType.ResultDisputed));
        Assert.False(handler.Handles(DeliveryType.CreateTableThread));
    }
}
