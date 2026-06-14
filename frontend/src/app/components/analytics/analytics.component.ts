import { Component, OnInit, inject, signal, computed } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { AnalyticsService, AnalyticsResponse, TopSessionStats, UserUsageStats } from '../../services/analytics.service';

@Component({
  selector: 'app-analytics',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './analytics.component.html',
  styleUrl: './analytics.component.scss'
})
export class AnalyticsComponent implements OnInit {
  private analyticsService = inject(AnalyticsService);

  data = signal<AnalyticsResponse | null>(null);
  isLoading = signal(true);
  error = signal<string | null>(null);
  expandedSessions = signal<Set<string>>(new Set());

  // Per-user add-tokens input values: userId -> amount string
  addTokenInputs = signal<Record<string, string>>({});
  // Which users have the add-tokens form open
  addTokensOpen = signal<Set<string>>(new Set());
  // Which users are currently submitting
  addingTokens = signal<Set<string>>(new Set());

  ngOnInit() {
    this.load();
  }

  load() {
    this.isLoading.set(true);
    this.analyticsService.getAnalytics().subscribe({
      next: res => { this.data.set(res); this.isLoading.set(false); },
      error: err => {
        this.error.set(err.status === 403 ? 'Access denied.' : 'Failed to load analytics.');
        this.isLoading.set(false);
      }
    });
  }

  toggleSession(sessionId: string) {
    this.expandedSessions.update(set => {
      const next = new Set(set);
      next.has(sessionId) ? next.delete(sessionId) : next.add(sessionId);
      return next;
    });
  }

  isExpanded(sessionId: string): boolean {
    return this.expandedSessions().has(sessionId);
  }

  toggleAddTokens(userId: string) {
    this.addTokensOpen.update(set => {
      const next = new Set(set);
      if (next.has(userId)) {
        next.delete(userId);
        this.addTokenInputs.update(m => { const n = {...m}; delete n[userId]; return n; });
      } else {
        next.add(userId);
      }
      return next;
    });
  }

  isAddTokensOpen(userId: string): boolean {
    return this.addTokensOpen().has(userId);
  }

  setAddTokenInput(userId: string, value: string) {
    this.addTokenInputs.update(m => ({ ...m, [userId]: value }));
  }

  getAddTokenInput(userId: string): string {
    return this.addTokenInputs()[userId] ?? '';
  }

  submitAddTokens(user: UserUsageStats) {
    const raw = this.getAddTokenInput(user.userId).replace(/,/g, '').trim();
    const amount = parseInt(raw, 10);
    if (!amount || amount <= 0) return;

    this.addingTokens.update(s => new Set([...s, user.userId]));
    this.analyticsService.addTokens(user.userId, amount).subscribe({
      next: result => {
        // Update the local data optimistically
        this.data.update(d => {
          if (!d) return d;
          return {
            ...d,
            userStats: d.userStats.map(u =>
              u.userId === user.userId
                ? {
                    ...u,
                    tokenLimit: result.newLimit,
                    tokensUsed: result.tokensUsed,
                    isExpired: result.newLimit > 0 && result.tokensUsed >= result.newLimit,
                    usagePercentage: result.newLimit > 0
                      ? Math.min(100, (result.tokensUsed / result.newLimit) * 100)
                      : 0
                  }
                : u
            )
          };
        });
        this.addingTokens.update(s => { const n = new Set(s); n.delete(user.userId); return n; });
        this.toggleAddTokens(user.userId);
      },
      error: () => {
        this.addingTokens.update(s => { const n = new Set(s); n.delete(user.userId); return n; });
      }
    });
  }

  isAdding(userId: string): boolean {
    return this.addingTokens().has(userId);
  }

  formatTokens(n: number): string {
    if (n >= 1_000_000) return (n / 1_000_000).toFixed(1) + 'M';
    if (n >= 1_000) return (n / 1_000).toFixed(1) + 'K';
    return n.toLocaleString();
  }

  formatCost(n: number): string {
    if (n === 0) return '$0.00';
    if (n < 0.0001) return '<$0.0001';
    if (n < 0.01) return '$' + n.toFixed(5);
    return '$' + n.toFixed(4);
  }

  formatDate(dateStr: string | null): string {
    if (!dateStr) return '—';
    const d = new Date(dateStr);
    const days = Math.floor((Date.now() - d.getTime()) / 86400000);
    if (days === 0) return 'Today';
    if (days === 1) return 'Yesterday';
    if (days < 7) return `${days}d ago`;
    return d.toLocaleDateString('en-US', { month: 'short', day: 'numeric' });
  }

  costBarWidth(cost: number, maxCost: number): string {
    if (maxCost === 0) return '0%';
    return Math.max(2, Math.round((cost / maxCost) * 100)) + '%';
  }

  maxUserCost = computed(() => Math.max(...(this.data()?.userStats ?? []).map(u => u.totalCost), 0));
  maxSessionCost = computed(() => Math.max(...(this.data()?.topSessions ?? []).map(s => s.cost), 0));
}
