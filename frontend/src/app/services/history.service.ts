import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';

export interface ChatSessionSummary {
  id: string;
  name: string;
  updatedAt: string;
  messageCount: number;
}

export interface SessionMessage {
  role: 'user' | 'assistant';
  content: string;
  sources?: string[];
  timestamp: string;
}

export interface ChatSessionDetail {
  id: string;
  name: string;
  messages: SessionMessage[];
  createdAt: string;
  updatedAt: string;
}

@Injectable({ providedIn: 'root' })
export class HistoryService {
  private http = inject(HttpClient);
  private baseUrl = `${environment.apiBaseUrl}/chathistory`;

  getSessions(): Observable<ChatSessionSummary[]> {
    return this.http.get<ChatSessionSummary[]>(this.baseUrl);
  }

  getSession(id: string): Observable<ChatSessionDetail> {
    return this.http.get<ChatSessionDetail>(`${this.baseUrl}/${id}`);
  }

  deleteSession(id: string): Observable<void> {
    return this.http.delete<void>(`${this.baseUrl}/${id}`);
  }

  clearAll(): Observable<void> {
    return this.http.delete<void>(this.baseUrl);
  }
}
