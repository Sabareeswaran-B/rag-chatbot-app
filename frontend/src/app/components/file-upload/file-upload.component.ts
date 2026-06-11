import { Component, OnInit, signal, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FileService, UploadedFile, UploadProgress } from '../../services/file.service';

interface FileUploadState {
  file: File;
  progress: number;
  status: 'pending' | 'uploading' | 'done' | 'error';
  message: string;
  chunksCreated?: number;
}

@Component({
  selector: 'app-file-upload',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './file-upload.component.html',
  styleUrl: './file-upload.component.scss'
})
export class FileUploadComponent implements OnInit {
  private fileService = inject(FileService);

  isDragOver = signal(false);
  uploadStates = signal<FileUploadState[]>([]);
  uploadedFiles = signal<UploadedFile[]>([]);
  isLoadingFiles = signal(false);

  acceptedTypes = '.pdf,.txt,.md,.docx,.csv';

  ngOnInit() {
    this.loadFiles();
  }

  loadFiles() {
    this.isLoadingFiles.set(true);
    this.fileService.getFiles().subscribe({
      next: (files) => {
        this.uploadedFiles.set(files);
        this.isLoadingFiles.set(false);
      },
      error: () => this.isLoadingFiles.set(false)
    });
  }

  onDragOver(event: DragEvent) {
    event.preventDefault();
    this.isDragOver.set(true);
  }

  onDragLeave(event: DragEvent) {
    event.preventDefault();
    this.isDragOver.set(false);
  }

  onDrop(event: DragEvent) {
    event.preventDefault();
    this.isDragOver.set(false);
    const files = Array.from(event.dataTransfer?.files || []);
    this.processFiles(files);
  }

  onFileSelect(event: Event) {
    const input = event.target as HTMLInputElement;
    const files = Array.from(input.files || []);
    this.processFiles(files);
    input.value = '';
  }

  private processFiles(files: File[]) {
    const valid = files.filter(f => this.isValidType(f));
    if (valid.length === 0) return;

    const newStates: FileUploadState[] = valid.map(f => ({
      file: f,
      progress: 0,
      status: 'pending',
      message: 'Queued...'
    }));

    this.uploadStates.update(states => [...newStates, ...states]);
    valid.forEach((file, i) => this.uploadFile(newStates[i], file));
  }

  private isValidType(file: File): boolean {
    const ext = '.' + file.name.split('.').pop()?.toLowerCase();
    return ['.pdf', '.txt', '.md', '.docx', '.csv'].includes(ext);
  }

  private uploadFile(state: FileUploadState, file: File) {
    state.status = 'uploading';
    state.message = 'Uploading...';

    this.fileService.uploadFile(file).subscribe({
      next: (progress: UploadProgress) => {
        this.uploadStates.update(states =>
          states.map(s => s === state
            ? { ...s, progress: progress.progress, status: progress.response ? (progress.response.success ? 'done' : 'error') : 'uploading',
                message: progress.response
                  ? (progress.response.success ? `Done! ${progress.response.chunksCreated} chunks indexed` : progress.response.error || 'Failed')
                  : `Uploading... ${progress.progress}%`,
                chunksCreated: progress.response?.chunksCreated }
            : s)
        );
        if (progress.response?.success) this.loadFiles();
      },
      error: () => {
        this.uploadStates.update(states =>
          states.map(s => s === state
            ? { ...s, status: 'error', message: 'Upload failed. Check backend connection.' }
            : s)
        );
      }
    });
  }

  deleteFile(fileName: string) {
    this.fileService.deleteFile(fileName).subscribe({
      next: () => this.loadFiles(),
      error: () => {}
    });
  }

  clearCompleted() {
    this.uploadStates.update(states => states.filter(s => s.status !== 'done' && s.status !== 'error'));
  }

  formatDate(dateStr: string): string {
    return new Date(dateStr).toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric', hour: '2-digit', minute: '2-digit' });
  }

  formatSize(bytes: number): string {
    if (bytes < 1024) return bytes + ' B';
    if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(1) + ' KB';
    return (bytes / (1024 * 1024)).toFixed(1) + ' MB';
  }
}
