import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';

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
  tokenLimit: number;
  tokensUsed: number;
  isExpired: boolean;
  usagePercentage: number;
  isBlocked: boolean;
}

export interface ViolationRecord {
  id: string;
  userId: string;
  username: string;
  isAnonymous: boolean;
  query: string;
  category: string;
  createdAt: string;
  isBlocked: boolean;
}

export interface RiskProfile {
  totalViolations: number;
  uniqueOffenders: number;
  blockedUsers: number;
  violations: ViolationRecord[];
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
  riskProfile: RiskProfile;
}

@Injectable({ providedIn: 'root' })
export class AnalyticsService {
  private http = inject(HttpClient);
  private baseUrl = `${environment.apiBaseUrl}/analytics`;

  getAnalytics(): Observable<AnalyticsResponse> {
    return this.http.get<AnalyticsResponse>(this.baseUrl);
  }

  addTokens(userId: string, amount: number): Observable<{ message: string; newLimit: number; tokensUsed: number }> {
    return this.http.patch<{ message: string; newLimit: number; tokensUsed: number }>(
      `${this.baseUrl}/users/${userId}/tokens`,
      { amount }
    );
  }

  blockUser(userId: string, blocked: boolean): Observable<{ message: string; blocked: boolean }> {
    return this.http.patch<{ message: string; blocked: boolean }>(
      `${this.baseUrl}/users/${userId}/block`,
      { blocked }
    );
  }
}
