export interface RewardSuggestion {
  suggestedReward: number
  minimumReward: number
  maximumReward: number
  currency: string
  factors: string[]
  dataConfidence: string
}

export async function requestRewardSuggestion(): Promise<RewardSuggestion> {
  const response = await fetch('/api/v1/reward-suggestions', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      category: 'delivery',
      distanceKilometers: 7.2,
      estimatedMinutes: 65,
      isPeakHours: false,
    }),
  })

  if (!response.ok) {
    throw new Error(`建议价服务返回 ${response.status}`)
  }

  return response.json() as Promise<RewardSuggestion>
}
