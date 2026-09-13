using AIToHuman.Domain.Orders;

namespace AIToHuman.Application.Admin;

/// <summary>
/// 运营跨用户检索订单（主要用于“待处置的争议”）。按状态过滤，按创建时间倒序。
/// 与 <see cref="IAdminTaskQuery" /> 一样，这里只做读取：不下判断、不改状态。
/// </summary>
public interface IAdminOrderQuery
{
    IReadOnlyCollection<Order> Search(OrderStatus? status, int limit);
}
