import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';

export interface AnalyticsSummary {
  totalInputTokens: number;
  totalOutputTokens: number;
  totalCost: number;
  totalUsers: number;
  totalSessions: number;
  totalMessages: number;
}

export interface UserUsageStats {
  userId: string;
  username: string;
  isAdmin: boolean;
  isAnonymous: boolean;
  totalInputTokens: number;
  totalOutputTokens: number;
  totalCost: number;
  sessionCount: number;
  messageCount: number;
  lastActivity: string | null;
}

export interface SessionMessageItem {
  role: string;
  content: string;
  sources: string[];
  timestamp: string;
}

export interface TopSessionStats {
  sessionId: string;
  sessionName: string;
  userId: string;
  username: string;
  inputTokens: number;
  outputTokens: number;
  cost: number;
  messageCount: number;
  updatedAt: string;
  messages: SessionMessageItem[];
}

export interface AnalyticsResponse {
  summary: AnalyticsSummary;
  userStats: UserUsageStats[];
  topSessions: TopSessionStats[];
}

@Injectable({ providedIn: 'root' })
export class AnalyticsService {
  private http = inject(HttpClient);
  private baseUrl = 'http://localhost:5000/api/analytics';

  getAnalytics(): Observable<AnalyticsResponse> {
    return this.http.get<AnalyticsResponse>(this.baseUrl);
  }
}
