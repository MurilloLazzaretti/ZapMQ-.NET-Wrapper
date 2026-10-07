using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace ZapMQ
{
    public delegate void EventDuplicateDiscarded(string pQueueName, ZapJSONMessage pMessage);

    public delegate void EventClaimFailure(string pQueueName, ZapJSONMessage pMessage, Exception pError);

    // Garante que cada mensagem seja processada por um unico consumidor.
    //
    // Por que isso e necessario
    // -------------------------
    // O servidor escolhe a proxima mensagem pendente e so marca o status depois
    // de serializa-la (TZapMethods.GetMessage). Nessa janela a mensagem continua
    // pendente, e um GetMessage concorrente de outro processo recebe a MESMA
    // mensagem. Com dois consumidores na mesma fila, o handler rodava duas vezes.
    //
    // A posse e decidida por um objeto nomeado do kernel: criar um objeto
    // nomeado e atomico, entao entre processos que pedem o mesmo nome no mesmo
    // instante exatamente um recebe createdNew = true. O Mutex nunca e
    // adquirido - so a existencia dele importa. Vale para processos no mesmo
    // servidor, sob contas que enxerguem o mesmo objeto.
    public class ZapMQMessageClaims
    {
        // A duplicata chega em milissegundos: os dois GetMessage acontecem
        // praticamente juntos. Dois minutos e folga de sobra.
        private const long RetentionMs = 120000;

        private class Claim
        {
            public Mutex Handle;
            public long ClaimedAtMs;
        }

        // Em ordem de posse, entao os mais antigos estao sempre na frente.
        private readonly Queue<Claim> Claims = new Queue<Claim>();
        private readonly Stopwatch Clock = Stopwatch.StartNew();

        public bool Enabled { get; set; } = true;
        public EventDuplicateDiscarded OnDuplicateDiscarded { get; set; }
        public EventClaimFailure OnClaimFailure { get; set; }

        // true = esta instancia deve processar a mensagem.
        //
        // Em qualquer falha do mecanismo a resposta e true: descartar uma
        // mensagem por causa da protecao seria pior que o defeito que ela
        // previne, porque o servidor nunca a reentrega. A falha e reportada
        // para nao deixar a protecao desligada em silencio.
        public bool TryClaim(string pQueueName, ZapJSONMessage pMessage)
        {
            if (!Enabled || string.IsNullOrEmpty(pMessage.Id))
            {
                return true;
            }

            Mutex handle;
            bool createdNew;
            try
            {
                handle = new Mutex(false, BuildName(pQueueName, pMessage.Id), out createdNew);
            }
            catch (Exception error)
            {
                ReportFailure(pQueueName, pMessage, error);
                return true;
            }

            if (!createdNew)
            {
                handle.Dispose();
                ReportDuplicate(pQueueName, pMessage);
                return false;
            }

            lock (Claims)
            {
                ReleaseExpired();
                Claims.Enqueue(new Claim { Handle = handle, ClaimedAtMs = Clock.ElapsedMilliseconds });
            }
            return true;
        }

        // O objeto so existe enquanto algum processo mantem um handle aberto;
        // fechar o ultimo o remove do kernel, sem nada para limpar em disco.
        private void ReleaseExpired()
        {
            long now = Clock.ElapsedMilliseconds;
            while ((Claims.Count > 0) && (now - Claims.Peek().ClaimedAtMs > RetentionMs))
            {
                Claims.Dequeue().Handle.Dispose();
            }
        }

        // "Global\" coloca o objeto no namespace compartilhado por todas as
        // sessoes: servicos rodam na sessao 0, mas um consumidor iniciado numa
        // sessao interativa tambem precisa enxerga-lo. A barra invertida so e
        // valida como separador do namespace.
        private static string BuildName(string pQueueName, string pIdMessage)
        {
            return "Global\\ZapMQ." + pQueueName.Replace('\\', '_') + "." + pIdMessage.Replace('\\', '_');
        }

        // Os callbacks sao codigo do consumidor: uma excecao neles nao pode
        // derrubar a thread de consumo nem mudar a decisao de posse.
        private void ReportDuplicate(string pQueueName, ZapJSONMessage pMessage)
        {
            try
            {
                OnDuplicateDiscarded?.Invoke(pQueueName, pMessage);
            }
            catch
            {
                //
            }
        }

        private void ReportFailure(string pQueueName, ZapJSONMessage pMessage, Exception pError)
        {
            try
            {
                OnClaimFailure?.Invoke(pQueueName, pMessage, pError);
            }
            catch
            {
                //
            }
        }
    }
}
