using CalCrony.Contracts;

namespace CalCrony.Bot.GameNights;

/// <summary>The seam the Game Nights extension uses to post its own kinds of outbox delivery without the
/// core poller knowing about them. The poller asks each registered handler, in turn, whether it owns a
/// delivery's type before falling back to its own dispatch. A handler follows the poller's contract: it
/// returns true once the delivery may be acknowledged, and throws to leave it pending for a retry.</summary>
public interface IExtensionDeliveryHandler
{
    /// <summary>Whether this handler posts deliveries of the given type.</summary>
    /// <param name="type">The delivery type.</param>
    /// <returns>True when this handler owns the type.</returns>
    bool Handles(DeliveryType type);

    /// <summary>Posts the delivery.</summary>
    /// <param name="delivery">The outbox row.</param>
    /// <returns>True when the delivery may be acknowledged.</returns>
    Task<bool> HandleAsync(DeliveryDto delivery);
}
