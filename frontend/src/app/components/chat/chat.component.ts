import { Component, OnInit, AfterViewChecked, ViewChild, ElementRef, signal, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ChatService, ChatResponse } from '../../services/chat.service';
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

  messages = signal<Message[]>([]);
  query = signal('');
  isLoading = signal(false);
  private typingSubscription: Subscription | null = null;

  ngOnInit() {
    this.addWelcomeMessage();
  }

  ngAfterViewChecked() {
    this.scrollToBottom();
  }

  private addWelcomeMessage() {
    this.messages.set([{
      id: '0',
      role: 'assistant',
      content: 'Hello! I\'m your RAG assistant. Upload some documents in the "File Upload" tab, then ask me questions about them.',
      displayContent: 'Hello! I\'m your RAG assistant. Upload some documents in the "File Upload" tab, then ask me questions about them.',
      isTyping: false,
      timestamp: new Date()
    }]);
  }

  sendMessage() {
    const q = this.query().trim();
    if (!q || this.isLoading()) return;

    const userMsg: Message = {
      id: Date.now().toString(),
      role: 'user',
      content: q,
      displayContent: q,
      isTyping: false,
      timestamp: new Date()
    };

    this.messages.update(msgs => [...msgs, userMsg]);
    this.query.set('');
    this.isLoading.set(true);

    const assistantMsg: Message = {
      id: (Date.now() + 1).toString(),
      role: 'assistant',
      content: '',
      displayContent: '',
      isTyping: true,
      timestamp: new Date()
    };
    this.messages.update(msgs => [...msgs, assistantMsg]);

    this.chatService.sendMessage(q).subscribe({
      next: (response: ChatResponse) => {
        this.isLoading.set(false);
        if (response.success) {
          this.typewriterEffect(assistantMsg.id, response.answer, response.sources, response.reasoning);
        } else {
          this.updateMessage(assistantMsg.id, response.error || 'An error occurred.', [], undefined, false);
        }
      },
      error: (err) => {
        this.isLoading.set(false);
        const errMsg = err.status === 0
          ? 'Cannot connect to the backend. Please ensure the server is running.'
          : 'Something went wrong. Please try again.';
        this.updateMessage(assistantMsg.id, errMsg, [], undefined, false);
      }
    });
  }

  private typewriterEffect(messageId: string, text: string, sources?: string[], reasoning?: string) {
    if (this.typingSubscription) this.typingSubscription.unsubscribe();

    let charIndex = 0;
    const speed = Math.max(10, Math.min(30, 2000 / text.length)); // adaptive speed

    this.typingSubscription = interval(speed).pipe(take(text.length)).subscribe({
      next: () => {
        charIndex++;
        this.messages.update(msgs => msgs.map(m =>
          m.id === messageId
            ? { ...m, displayContent: text.slice(0, charIndex), isTyping: true }
            : m
        ));
      },
      complete: () => {
        this.updateMessage(messageId, text, sources || [], reasoning, false);
      }
    });
  }

  private updateMessage(id: string, content: string, sources: string[], reasoning: string | undefined, isTyping: boolean) {
    this.messages.update(msgs => msgs.map(m =>
      m.id === id ? { ...m, content, displayContent: content, sources, reasoning, isTyping } : m
    ));
  }

  onKeyDown(event: KeyboardEvent) {
    if (event.key === 'Enter' && !event.shiftKey) {
      event.preventDefault();
      this.sendMessage();
    }
  }

  private scrollToBottom() {
    try {
      this.messagesEnd?.nativeElement.scrollIntoView({ behavior: 'smooth' });
    } catch {}
  }

  clearChat() {
    if (this.typingSubscription) this.typingSubscription.unsubscribe();
    this.addWelcomeMessage();
  }
}
