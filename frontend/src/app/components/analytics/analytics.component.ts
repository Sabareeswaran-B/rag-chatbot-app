import { Component, OnInit, inject, signal, computed } from '@angular/core';
import { CommonModule } from '@angular/common';
import { AnalyticsService, AnalyticsResponse, TopSessionStats } from '../../services/analytics.service';

@Component({
  selector: 'app-analytics',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './analytics.component.html',
  styleUrl: './analytics.component.scss'
})
export class AnalyticsComponent implements OnInit {
  private analyticsService = inject(AnalyticsService);

  data = signal<AnalyticsResponse | null>(null);
  isLoading = signal(true);
  error = signal<string | null>(null);
  expandedSessions = signal<Set<string>>(new Set());

  ngOnInit() {
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

  maxUserCost = computed(() => {
    const stats = this.data()?.userStats ?? [];
    return Math.max(...stats.map(u => u.totalCost), 0);
  });

  maxSessionCost = computed(() => {
    const sessions = this.data()?.topSessions ?? [];
    return Math.max(...sessions.map(s => s.cost), 0);
  });
}
