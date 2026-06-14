import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';

export interface ChatRequest {
  query: string;
  sessionId?: string | null;
}

export interface ChatResponse {
  answer: string;
  sources: string[];
  reasoning?: string;
  success: boolean;
  error?: string;
  sessionId?: string;
  sessionName?: string;
}

@Injectable({ providedIn: 'root' })
export class ChatService {
  private http = inject(HttpClient);
  private baseUrl = environment.apiBaseUrl;

  sendMessage(query: string, sessionId?: string | null): Observable<ChatResponse> {
    return this.http.post<ChatResponse>(`${this.baseUrl}/chat`, { query, sessionId });
  }
}
