namespace AIToHuman.Domain.Tasks;

/// <summary>
/// 两个草稿版本之间某个字段的变化：字段名（面向人）+ 之前的值 + 之后的值。
/// 值统一做成人可读的字符串（时间带 UTC、金额带币种、验收标准用分号连接），
/// 这样前端不需要再实现一套格式化规则。
/// </summary>
public sealed record TaskDraftFieldChange(string Field, string? Before, string? After);
