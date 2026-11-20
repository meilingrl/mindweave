import unittest
from unittest.mock import patch, MagicMock
from multi_probe import choose, parse_stream, parse_plain, BASES, open_request

class ProtocolTests(unittest.TestCase):
    def test_separate_endpoints(self):
        for family, expected in [('gpt','https://relay.example.com/v1/models'), ('claude','https://relay.example.com/v1/models'), ('deepseek','https://api.deepseek.com/models')]:
            opener=MagicMock()
            with patch('urllib.request.build_opener', return_value=opener):
                open_request(BASES[family], family+'-test-only', '/models')
            req=opener.open.call_args.args[0]
            self.assertEqual(req.full_url, expected)
            self.assertEqual(req.get_header('Authorization'), 'Bearer '+family+'-test-only')
    def test_selection_only_catalog(self):
        self.assertEqual(choose(['gpt-6.1-sol', 'claude-sonnet-demo', 'deepseek-chat'])['claude'], 'claude-sonnet-demo')
        self.assertIsNone(choose(['gpt-6.1-sol'])['deepseek'])
    def test_chat_complete(self):
        self.assertEqual(parse_stream('chat', [{'choices':[{'delta':{'content':'好'},'finish_reason':None}]}, {'choices':[{'delta':{},'finish_reason':'stop'}]}, '[DONE]'])['status'], 'pass')
    def test_truncation_not_success(self):
        self.assertNotEqual(parse_stream('chat', [{'choices':[{'delta':{'content':'好'},'finish_reason':'length'}]}, '[DONE]'])['status'], 'pass')
    def test_eof_not_success(self):
        self.assertNotEqual(parse_stream('messages', [{'type':'content_block_delta','delta':{'type':'text_delta','text':'好'}}])['status'], 'pass')
    def test_messages_complete(self):
        self.assertEqual(parse_stream('messages', [{'type':'content_block_delta','delta':{'type':'text_delta','text':'好'}},{'type':'message_delta','delta':{'stop_reason':'end_turn'}},{'type':'message_stop'}])['status'], 'pass')
    def test_plain_requires_text_and_finish(self):
        self.assertEqual(parse_plain('messages', {'content':[{'type':'text','text':'好'}], 'stop_reason':'end_turn'})['status'], 'pass')
        self.assertNotEqual(parse_plain('chat', {'choices':[{'message':{'content':''},'finish_reason':'stop'}]})['status'], 'pass')

if __name__ == '__main__': unittest.main()
