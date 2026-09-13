/**
 * 时间显示：主应用与运营后台共用同一套格式（本地时区的“月-日 时:分”），
 * 免得两个页面各写一份、时间口径慢慢漂移。
 */
export function formatDeadline(value: string) {
  return new Intl.DateTimeFormat('zh-CN', { month: 'numeric', day: 'numeric', hour: '2-digit', minute: '2-digit' }).format(new Date(value))
}
