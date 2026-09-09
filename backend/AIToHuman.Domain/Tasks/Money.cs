using AIToHuman.Domain.Common;

namespace AIToHuman.Domain.Tasks;

public readonly record struct Money
{
    public Money(decimal amount, string currency = "CNY")
    {
        if (amount <= 0)
        {
            throw new DomainException("悬赏金额必须大于零。");
        }

        if (string.IsNullOrWhiteSpace(currency) || currency.Length != 3)
        {
            throw new DomainException("币种必须是三位代码。");
        }

        Amount = decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
        Currency = currency.ToUpperInvariant();
    }

    public decimal Amount { get; }
    public string Currency { get; }
}
