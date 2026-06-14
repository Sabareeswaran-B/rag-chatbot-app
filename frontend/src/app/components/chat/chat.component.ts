import { Component, OnInit, AfterViewChecked, ViewChild, ElementRef, signal, inject, computed } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ChatService, ChatResponse } from '../../services/chat.service';
import { HistoryService, ChatSessionSummary } from '../../services/history.service';
import { interval, Subscription } from 'rxjs';
import { take } from 'rxjs/operators';

export interface Message {
  id: string;
  role: 'user' | 'assistant';
  content: string;
  displayContent: string;
  sources?: string[];
  reasoning?: string;
  isTyping: boolean;
  timestamp: Date;
}

@Component({
  selector: 'app-chat',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './chat.component.html',
  styleUrl: './chat.component.scss'
})
export class ChatComponent implements OnInit, AfterViewChecked {
  @ViewChild('messagesEnd') messagesEnd!: ElementRef;

  private chatService = inject(ChatService);
  private historyService = inject(HistoryService);

  messages = signal<Message[]>([]);
  query = signal('');
  isLoading = signal(false);
  currentSessionId = signal<string | null>(null);
  private typingSubscription: Subscription | null = null;

  sidebarOpen = signal(true);
  showSearch = signal(false);
  searchQuery = signal('');
  sessions = signal<ChatSessionSummary[]>([]);
  isLoadingHistory = signal(false);

  filteredSessions = computed(() => {
    const q = this.searchQuery().toLowerCase().trim();
    if (!q) return this.sessions();
    return this.sessions().filter(s => s.name.toLowerCase().includes(q));
  });

  ngOnInit() {
    this.addWelcomeMessage();
    this.loadHistory();
  }

  ngAfterViewChecked() {
    this.scrollToBottom();
  }

  loadHistory() {
    this.isLoadingHistory.set(true);
    this.historyService.getSessions().subscribe({
      next: sessions => { this.sessions.set(sessions); this.isLoadingHistory.set(false); },
      error: () => this.isLoadingHistory.set(false)
    });
  }

  loadSession(session: ChatSessionSummary) {
    if (this.currentSessionId() === session.id) return;
    this.historyService.getSession(session.id).subscribe({
      next: detail => {
        this.currentSessionId.set(session.id);
        this.messages.set(detail.messages.map((m, i) => ({
          id: `${session.id}-${i}`,
          role: m.role,
          content: m.content,
          displayContent: m.content,
          sources: m.sources,
          isTyping: false,
          timestamp: new Date(m.timestamp)
        })));
      }
    });
  }

  newChat() {
    if (this.typingSubscription) this.typingSubscription.unsubscribe();
    this.currentSessionId.set(null);
    this.addWelcomeMessage();
  }

  deleteSession(event: Event, sessionId: string) {
    event.stopPropagation();
    this.historyService.deleteSession(sessionId).subscribe({
      next: () => {
        this.sessions.update(s => s.filter(x => x.id !== sessionId));
        if (this.currentSessionId() === sessionId) this.newChat();
      }
    });
  }

  clearAll() {
    this.historyService.clearAll().subscribe({
      next: () => { this.sessions.set([]); this.newChat(); }
    });
  }

  toggleSidebar() { this.sidebarOpen.update(v => !v); }

  toggleSearch() {
    this.showSearch.update(v => !v);
    if (!this.showSearch()) this.searchQuery.set('');
  }

  private addWelcomeMessage() {
    this.messages.set([{
      id: '0', role: 'assistant',
      content: 'Hello! I\'m your RAG assistant. Upload some documents in the "File Upload" tab, then ask me questions about them.',
      displayContent: 'Hello! I\'m your RAG assistant. Upload some documents in the "File Upload" tab, then ask me questions about them.',
      isTyping: false, timestamp: new Date()
    }]);
  }

  sendMessage() {
    const q = this.query().trim();
    if (!q || this.isLoading()) return;

    this.messages.update(msgs => [...msgs, {
      id: Date.now().toString(), role: 'user', content: q,
      displayContent: q, isTyping: false, timestamp: new Date()
    }]);
    this.query.set('');
    this.isLoading.set(true);

    const assistantId = (Date.now() + 1).toString();
    this.messages.update(msgs => [...msgs, {
      id: assistantId, role: 'assistant', content: '',
      displayContent: '', isTyping: true, timestamp: new Date()
    }]);

    this.chatService.sendMessage(q, this.currentSessionId()).subscribe({
      next: (response: ChatResponse) => {
        this.isLoading.set(false);
        if (response.success) {
          if (response.sessionId) {
            const isNew = this.currentSessionId() === null;
            this.currentSessionId.set(response.sessionId);
            if (isNew) {
              this.sessions.update(s => [{
                id: response.sessionId!, name: response.sessionName || 'New Chat',
                updatedAt: new Date().toISOString(), messageCount: 1
              }, ...s.slice(0, 29)]);
            } else {
              this.sessions.update(s => s.map(x =>
                x.id === response.sessionId
                  ? { ...x, updatedAt: new Date().toISOString(), messageCount: (x.messageCount || 0) + 1 }
                  : x
              ));
            }
          }
          this.typewriterEffect(assistantId, response.answer, response.sources, response.reasoning);
        } else {
          this.updateMessage(assistantId, response.error || 'An error occurred.', [], undefined, false);
        }
      },
      error: (err) => {
        this.isLoading.set(false);
        const msg = err.status === 0
          ? 'Cannot connect to the backend. Please ensure the server is running.'
          : 'Something went wrong. Please try again.';
        this.updateMessage(assistantId, msg, [], undefined, false);
      }
    });
  }

  private typewriterEffect(messageId: string, text: string, sources?: string[], reasoning?: string) {
    if (this.typingSubscription) this.typingSubscription.unsubscribe();
    let charIndex = 0;
    const speed = Math.max(10, Math.min(30, 2000 / text.length));
    this.typingSubscription = interval(speed).pipe(take(text.length)).subscribe({
      next: () => {
        charIndex++;
        this.messages.update(msgs => msgs.map(m =>
          m.id === messageId ? { ...m, displayContent: text.slice(0, charIndex), isTyping: true } : m
        ));
      },
      complete: () => this.updateMessage(messageId, text, sources || [], reasoning, false)
    });
  }

  private updateMessage(id: string, content: string, sources: string[], reasoning: string | undefined, isTyping: boolean) {
    this.messages.update(msgs => msgs.map(m =>
      m.id === id ? { ...m, content, displayContent: content, sources, reasoning, isTyping } : m
    ));
  }

  onKeyDown(event: KeyboardEvent) {
    if (event.key === 'Enter' && !event.shiftKey) { event.preventDefault(); this.sendMessage(); }
  }

  private scrollToBottom() {
    try { this.messagesEnd?.nativeElement.scrollIntoView({ behavior: 'smooth' }); } catch {}
  }

  formatSessionDate(dateStr: string): string {
    const d = new Date(dateStr);
    const days = Math.floor((Date.now() - d.getTime()) / 86400000);
    if (days === 0) return 'Today';
    if (days === 1) return 'Yesterday';
    if (days < 7) return `${days}d ago`;
    return d.toLocaleDateString('en-US', { month: 'short', day: 'numeric' });
  }
}
